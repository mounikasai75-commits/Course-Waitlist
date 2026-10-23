# Course Waitlist Technical Design

## Goals and boundaries

Design a runnable .NET 8 service for enrollment, dropping, and FIFO waitlist promotion for one course offering. The design uses ASP.NET Core Minimal API, EF Core, and SQLite in-memory.

Keep the implementation to one API project and focused tests. No frontend, authentication, external integrations, deployment infrastructure, configurable eligibility engine, or EF Core InMemory provider is included. The workspace has no starter project, so these are proposed structures and conventions rather than a migration plan.

## Proposed project structure

```text
CourseWaitlist.sln
src/
  CourseWaitlist.Api/
    Program.cs
    Data/
      CourseWaitlistDbContext.cs
      SqliteDatabase.cs
    Domain/
      CourseOffering.cs
      Learner.cs
      Registration.cs
      RegistrationStatus.cs
    Services/
      IRegistrationService.cs
      RegistrationService.cs
      RegistrationResults.cs
      CourseState.cs
    Data/ (also)
      CourseSettings.cs        # configured course id, capacity, known learners
      CourseSeeder.cs          # idempotent startup seed
    Endpoints/
      CourseEndpoints.cs
tests/
  CourseWaitlist.Tests/
    TestDatabase.cs, DatabaseInvariants.cs
    PersistenceTests.cs, ApplicationSetupTests.cs
    EnrollmentServiceTests.cs, DropServiceTests.cs
    ConcurrencyTests.cs, CourseEndpointTests.cs, AcceptanceGapTests.cs
```

Keep persistence mapping in the DbContext, workflow orchestration in one registration service, and HTTP mapping in endpoint handlers. Do not introduce a repository layer or mediator unless the eventual starter has an established convention that warrants it.

## Domain model and relationships

### `CourseOffering`

- `Id`: offering identifier.
- `Capacity`: non-negative maximum number of enrolled learners.
- `NextWaitlistSequence`: next monotonic queue sequence to allocate.
- Navigation to registrations.

The assessment concerns one offering. Retaining an offering ID keeps endpoint and relational boundaries explicit without introducing catalog or multi-term features. Capacity is configured/seeded, not managed through a new public endpoint.

### `Learner`

- `Id`: learner identifier.
- `PrerequisiteSatisfied`: simple pass/fail value used by this bounded feature.
- Navigation to registrations.

**Approval needed:** the assessment does not define the actual prerequisite or its source of truth. This proposal treats a boolean learner property as the smallest usable representation. It does not model prerequisite courses or make the rule configurable. The value may change while a learner waits; promotion reads its current persisted value.

### `Registration`

- `Id`: primary key.
- `OfferingId`, `LearnerId`: foreign keys.
- `Status`: `Enrolled`, `Waitlisted`, or `Dropped`.
- `WaitlistSequence`: nullable monotonic sequence; set only while waitlisted.
- `EnqueuedAtTicks`: UTC ticks when the learner joined the queue; informational ("joined at"), never used for ordering; meaningful only while waitlisted.

Relationships: each registration belongs to one offering and one learner. One row per learner/offering is retained, including after a drop. A permitted re-enrollment reuses the dropped row and assigns a fresh waitlist sequence if queued, avoiding registration history that the feature does not require.

**Approval needed:** REQUIREMENTS.md records fresh enrollment after dropping as an assumption. If not approved, remove or alter this reactivation behavior before implementation.

## States and transitions

- **Not registered → Enrolled:** request valid, prerequisite passes, a seat exists, and no older eligible waiter has priority.
- **Not registered → Waitlisted:** request valid and eligible, with no seat left after older eligible waiters are served; append to the queue. If a seat is open but an older eligible waiter exists, that waiter is promoted first, and the requester is enrolled only if a seat still remains, otherwise appended.
- **Enrolled → Dropped:** valid drop; release the seat and atomically promote the earliest currently eligible waiters into any open seats (normally one).
- **Waitlisted → Enrolled:** promotion selects this learner as the first eligible registration in queue order; clear `WaitlistSequence`.
- **Dropped → Enrolled/Waitlisted:** a new enrollment request, if the proposed re-enrollment assumption is approved, is processed as a fresh request.
- **Waitlisted, eligibility changes to false:** no state or queue position change; skip the learner when selecting someone for promotion.
- **Waitlisted, eligibility becomes true again:** no state change; their original sequence remains and determines their priority.
- Duplicate enrollment, invalid request, failed prerequisite on a new request, or drop without an enrolled registration causes no state mutation.

An active registration is exactly one `Enrolled` or `Waitlisted` row for a learner/offering. Dropped registrations are inactive.

## Service boundary and contracts

The service receives a scoped `CourseWaitlistDbContext`. All state-changing entry points acquire the shared database-operation gate, execute within a transaction, save, and release the gate in `finally`/`await using` disposal. The gate serializes use of the shared SQLite connection as well as service mutations.

Illustrative contracts (not implementation code):

```text
IRegistrationService
  EnrollAsync(offeringId, learnerId, cancellationToken)
    -> Task<EnrollmentResult>

  DropAsync(offeringId, learnerId, cancellationToken)
    -> Task<DropResult>
```

`EnrollmentResult` should carry a typed outcome and relevant registration state. Outcomes: `Enrolled`, `Waitlisted`, `AlreadyRegistered`, `PrerequisiteNotMet`, `OfferingNotFound`, `LearnerNotFound`, and `InvalidRequest`. If an older queued learner is promoted while processing a new request, return the requester's resulting state and optionally the promoted learner ID so the transition is observable.

`DropResult` outcomes: `DroppedAndPromoted`, `DroppedSeatAvailable`, `DroppedFromWaitlist`, `NotEnrolled`, `OfferingNotFound`, `LearnerNotFound`, and `InvalidRequest`. A waitlisted learner's drop removes the queue entry (`DroppedFromWaitlist`) without affecting capacity or promoting anyone. A not-enrolled or repeated drop is a no-op outcome, not an exception.

Cancellation and unexpected database failures should propagate to standard ASP.NET Core error handling; do not convert them into success-shaped domain results. Expected business outcomes are represented explicitly by typed results.

## Minimal API endpoints

The API exposes the single configured course (`Course` section of `appsettings.json`: id, capacity, and the known learners with a pass/fail prerequisite flag, seeded idempotently at startup). Learner IDs are the only input; there is no offering ID in the routes. Handlers live in `Endpoints/CourseEndpoints.cs`, only parse input and map typed service outcomes to HTTP; all rules stay in `IRegistrationService`.

| Method and route | Purpose | Request | Outcomes |
|---|---|---|---|
| `POST /course/enroll` | Enroll or waitlist a learner, applying FIFO fairness and eligibility. | JSON `{ "learnerId": "..." }` | `200` `Enrolled` or `Waitlisted` (body: `outcome`, `learnerId`, `status`, `waitlistSequence`, `promotedLearnerIds`, `message`); `409` `AlreadyRegistered`; `422` `PrerequisiteNotMet`; `400` `InvalidRequest` (missing/blank/non-string `learnerId`, malformed JSON, non-JSON content type); `404` `LearnerNotFound` / `OfferingNotFound`. |
| `POST /course/drop` | Drop an enrolled or waitlisted learner; promote the earliest eligible waiter if a seat opened. | JSON `{ "learnerId": "..." }` | `200` `DroppedAndPromoted`, `DroppedSeatAvailable`, `DroppedFromWaitlist` (body: `outcome`, `learnerId`, `promotedLearnerId`, `promotedLearnerIds`, `message`); `409` `NotEnrolled` (error body `{ "code", "message" }`) for a repeated/unnecessary drop (no state change); `400` `InvalidRequest`; `404` `LearnerNotFound` / `OfferingNotFound`. |
| `GET /course/state` | Read-only snapshot. | None | `200` `{ offeringId, capacity, enrolledCount, availableSeats, enrolled: [ids], waitlist: [{ position, learnerId, sequence, joinedAtUtc }] }`; `404` if the offering is missing. |

Decisions taken here: a repeated or non-enrolled drop is `409` with code `NotEnrolled` and no state change (approved by the developer); an unknown learner's drop is `404`; both `Enrolled` and `Waitlisted` are `200` and distinguished by `outcome`. Error bodies are always `{ "code": "...", "message": "..." }`. Unexpected exceptions return `500` with `{ "code": "InternalError", ... }` through a central handler, without exception details. `GET /course/state` reads through the service under the same gate as writers, so it is a consistent snapshot (`IRegistrationService.GetStateAsync`).

No endpoint is added to edit learner prerequisites, configure capacity, or administer the waitlist. Tests or seed data can change prerequisite status to verify ineligible waitlist retention and later regained eligibility.

## Database model and constraints

Use EF Core's SQLite provider and create the schema at startup (`EnsureCreated` for this small assessment unless a starter migration convention is discovered). Configure:

- Primary keys on all entities.
- Foreign keys from `Registration` to `CourseOffering` and `Learner`.
- Unique index on `(OfferingId, LearnerId)`, enforcing at most one row and therefore at most one active registration per learner/offering.
- Check constraint `Capacity >= 0`.
- Check constraint requiring `WaitlistSequence IS NOT NULL` only for `Waitlisted` and null for `Enrolled`/`Dropped`.
- Unique index on `(OfferingId, WaitlistSequence)` to ensure a stable queue order without duplicate sequence values. SQLite permits multiple `NULL` values in a unique index.
- Check constraint restricting `Status` to the defined enum values if stored as an integer/string in a way that permits such a constraint.

The service validates foreign keys and business rules before saving; database constraints remain a final integrity backstop. Treat uniqueness/constraint violations as conflicts only when they correspond to a known domain race; otherwise log and surface an appropriate server error rather than hiding data or programming errors.

## FIFO ordering strategy

Queue order is the monotonically increasing `WaitlistSequence` per offering (approved by the developer). Allocate it from `CourseOffering.NextWaitlistSequence` and increment it inside the serialized enrollment transaction. Never derive the next value from currently waiting rows: a promoted learner's sequence is cleared, but later arrivals must not reuse old sequence numbers. Order by `WaitlistSequence` ascending. The unique `(OfferingId, WaitlistSequence)` index guarantees no duplicate positions.

Each queue entry also records `EnqueuedAtTicks` (UTC ticks from an injectable `TimeProvider`, `TimeProvider.System` by default), shown in `GET /course/state` as `joinedAtUtc`. It is informational only and never used for ordering, so a system clock change cannot reorder the queue. Using it as an ordering key would add nothing: any clock that never goes backwards yields the same order as the sequence, and one that can go backwards would break FIFO.

When promoting, query waitlisted registrations in sequence order, filtered by each learner's current `PrerequisiteSatisfied` value, and take as many as there are open seats (one for a normal drop). Do not update, delete, or renumber skipped ineligible registrations. Promotion clears the selected learner's sequence while changing status to `Enrolled`.

Initialize the sequence to one. Sequence exhaustion is not practically relevant for this assessment. A dropped learner who re-enters the queue receives a new sequence and a new timestamp, not their old position.

## Enrollment and prerequisite algorithm

Within one serialized transaction:

1. Validate route/body values and load the offering and learner; return typed invalid/not-found outcomes before mutation.
2. Find the learner's registration for the offering. If status is `Enrolled` or `Waitlisted`, return `AlreadyRegistered` without mutation.
3. Check the learner's current prerequisite flag. If false, return `PrerequisiteNotMet`; do not enroll or waitlist.
4. Count `Enrolled` registrations and compute open seats (`capacity - enrolled`, never negative).
5. Select up to that many currently eligible waiters in FIFO (sequence) order and promote them (`Enrolled`, sequence cleared). Ineligible waiters are skipped and left untouched.
6. If open seats remain after those promotions, mark the learner `Enrolled`. Otherwise assign the next FIFO sequence and mark the learner `Waitlisted` at the queue tail.
7. Save and commit everything together. Return the requester's outcome and the promoted learner IDs (`PromotedLearnerIds`).

This ensures a new eligible learner cannot claim an open seat ahead of an older eligible waiter. If the offering is full, nothing is promoted merely because a new request arrived.

## Drop and promotion algorithm

Within one serialized transaction:

1. Validate and load offering, learner, and registration.
2. If the registration is not active, return `NotEnrolled` without mutation.
3. Mark the learner's registration `Dropped` and clear its queue sequence. If it was `Waitlisted`, commit and stop (no seat freed).
4. Compute open seats as `capacity - enrolled` (excluding the dropped row). This can exceed one if an earlier drop left a seat open because every waiter was ineligible.
5. Query waitlisted registrations whose learner currently satisfies the prerequisite, ordered by sequence, taking at most the open seats. Leave ineligible entries and their sequences untouched. Transition each selected waiter to `Enrolled` and clear its sequence. If none is eligible, leave the seats open.
6. Save and commit the drop and promotions together. `DropResult.PromotedLearnerIds` lists everyone promoted (`PromotedLearnerId` is the first).

The transaction prevents a failed promotion/save from leaving a drop half-applied.

## Preventing over-enrollment and duplicates

Over-enrollment is prevented by checking the enrolled count and applying the resulting enrollment/promotion changes inside a write transaction while holding a process-wide asynchronous gate. The gate serializes all service operations that use the single shared SQLite connection; concurrent API requests therefore cannot both observe the same final seat and claim it. Capacity check, drop, and promotion are committed atomically.

The unique `(OfferingId, LearnerId)` database index prevents duplicate registration rows even if application validation is bypassed. Service logic treats `Enrolled` and `Waitlisted` as duplicates, and never creates another row. A dropped row may be reactivated only under the re-enrollment assumption above.

This concurrency guarantee is deliberately single-process. A process-local gate does not coordinate multiple app processes. Multi-instance coordination is outside this in-memory assessment scope; do not deploy or claim the design as a shared multi-process registration database. If concurrency across processes becomes required, replace the in-memory storage arrangement with a shared persistent database and an appropriate serialization/locking strategy.

## SQLite in-memory lifetime and request handling

SQLite `:memory:` databases exist only for the lifetime of their connection. Create and open one `SqliteConnection` when the application starts, register that open connection as a singleton, and configure scoped `CourseWaitlistDbContext` instances to use it. Do not dispose/reopen it between HTTP requests. Dispose it only when the host shuts down.

Each HTTP request receives its own scoped DbContext, but DbContext and the shared connection are not thread-safe. All database access performed by the feature service must be guarded by the same singleton `SemaphoreSlim` for the full operation, including reads that share the connection. Endpoint handlers must not query the DbContext directly outside this service. Initialize schema and seed the offering/learners before accepting requests.

For tests, create a separate open `SqliteConnection("Data Source=:memory:")` per test (or per test fixture where sequential reuse is intentional), create schema/seed data on it, and dispose it after the test. Each connection provides an isolated database. Do not use one shared connection across parallel tests. Give each test's service its own gate and DbContext options. For API integration tests, the test host should own the connection for the host lifetime and dispose it when the host is disposed.

## Transaction and concurrency handling

- Use an EF Core database transaction around every state-changing enrollment/drop operation, including capacity check, sequence allocation, state transitions, and promotion.
- Acquire the singleton async gate before any operation that accesses the shared connection and release it after transaction/context work completes.
- Never share a DbContext across requests, threads, or parallel tests.
- If SQLite reports a busy/locked or constraint error despite the gate, roll back and surface/log it; do not silently retry a mutation unless a bounded, documented retry policy is added and tested.
- This serializes a single app host. It is not a distributed lock and does not provide cross-process guarantees.

### Concurrency review (implemented)

Findings and changes:

- The gate plus the transaction is what prevents over-enrollment, duplicate registrations and sequence collisions. The unique indexes and CHECK constraints are only a backstop: a check-then-insert race would be caught as an exception, not handled as a clean result.
- Stale tracking: a DbContext reused across calls could reuse a stale `Learner` or `NextWaitlistSequence`. Both service methods now call `ChangeTracker.Clear()` after acquiring the gate, so every operation reads current rows. Regression tests cover both cases.
- Enrollment now performs the approved promotion step. When seats are free, the earliest eligible waiters are promoted first (in sequence order, up to the free seats), and the newcomer is enrolled only if a seat remains, otherwise queued at the tail. `EnrollmentResult.PromotedLearnerIds` reports who was promoted. A free seat with an eligible waiter should not normally occur (drops promote immediately), but this keeps the no-bypass rule true if it does, for example after a learner becomes eligible again.
- Gate acquisition happens before the `try`, so a cancelled waiter never releases a gate it does not hold.

Verified by `ConcurrencyTests` (each scenario uses its own database, gate, and one DbContext per operation; final state is checked by `DatabaseInvariants.AssertConsistent`: one row per learner, enrolled <= capacity, unique non-null sequences only on waitlisted rows, counter above every sequence, no free seat alongside an eligible waiter): racing for one seat, repeated same-learner requests, repeated drops, drop vs enroll (with and without waiters), same learner drop vs enroll, concurrent waitlist joins/drops, skipped ineligible waiters, a seeded mixed workload, gate waiting and cancellation.

Limitations:

- Single process only. The gate is process-local; multiple instances, or other processes using the database, are not protected. A real deployment would need a shared database and a database-level strategy (for example a transaction with a write lock plus retry).
- All access to the shared connection must go through the service. A context used outside the gate could join an open transaction on the same connection.
- Operations are fully serialized, so throughput is limited to one at a time. This is acceptable for a single course offering.
- No retry policy. An unexpected busy/constraint error rolls back and surfaces as a failure.
- Tests cannot prove the absence of races; they repeat contended scenarios and use a fixed-seed workload. Interleavings depend on the scheduler.
- Eligibility changes made outside the service while operations are running are read at the time of each operation; the concurrency tests do not toggle eligibility mid-flight.

## Error handling and result types

Return typed domain results for expected conditions: enrollment, waitlisting, already registered, prerequisite failure, invalid input, missing offering/learner, successful drop with/without promotion, and not enrolled. Minimal API handlers map those outcomes to the chosen HTTP contract.

Validate route/body identifiers and required values at the boundary. Expected validation/business failures must not throw generic exceptions or mutate the database. Unexpected exceptions are logged by standard ASP.NET Core infrastructure and returned through centralized exception handling as a generic server error; internal exception content is not sent to clients. A request cancelled by a disconnecting client is logged at information level (status 499, no body) rather than as a server error; its transaction rolls back and the gate is released. Ensure transaction disposal rolls back any failed operation.

## Invariants and focused verification

Tests should establish:

- `Enrolled` count is never greater than `Capacity`.
- A learner/offering pair has no more than one registration row and at most one active state.
- Prerequisite failure blocks new enrollment and waitlisting.
- FIFO promotes the first currently eligible learner; ineligible earlier entries stay queued at the same sequence.
- Restored eligibility lets a retained waiter keep original priority.
- A new eligible learner cannot bypass an older eligible waiter when a seat is open.
- Drop and promotion are atomic; no eligible waiter leaves the seat open.
- Duplicate enrollment, invalid requests, failed prerequisites, and repeated/non-enrolled drops do not corrupt state.
- Parallel enrollment attempts for the final seat produce no over-enrollment under the single-host gate.
- Each test's SQLite database is isolated from other tests.

Setup, run, and test commands are in README.md; the acceptance-criteria-to-test mapping is in TESTING.md; the AI prompt/use and verification record is in AI_USAGE.md.

## Decisions confirmed by the developer

1. `Learner.PrerequisiteSatisfied` is the simple pass/fail prerequisite representation, seeded from `appsettings.json`.
2. A learner may make a fresh request after dropping, subject to current eligibility and FIFO rules; the dropped registration row is reused.
3. HTTP mapping: `409` for `AlreadyRegistered`, `409` with code `NotEnrolled` for a repeated/non-enrolled drop, and `404` for a drop by an unknown learner.
4. Enroll-time promotion of older eligible waiters when a seat is open, as implemented.
5. The concurrency scope is one running service process. SQLite in-memory state is process-local and the gate is not distributed.
