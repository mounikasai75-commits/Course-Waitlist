# AI Usage Record

AI assistant: an AI assistant using Copilot SDK in VS Code. It was used for analysis, design, implementation, testing, and review of the Course Waitlist feature.

How to read this record:

- **Prompt** is a compact form of what the developer submitted: the role, context, task, guardrails, and expected output.
- **AI Response** is a short factual summary of what the assistant did or reported. It is not a transcript.
- **Developer Decision** is Accepted / Modified / Rejected. Decisions that changed the assistant's work are named.
- **Verification** lists only commands that were actually executed and the results that were observed. Documentation-only phases say so.

No credentials or sensitive data were included in any prompt. The temporary .NET 8 SDK used to build and test (no SDK was installed on the machine) was downloaded per phase and deleted afterwards.

## Summary of decisions that changed AI output

| Interaction | What changed |
|---|---|
| 05 | Plain `Data Source=:memory:` connection string chosen over shared-cache, because shared cache would break per-test isolation. |
| 07 | Waitlisted learner's drop now removes the queue entry (`DroppedFromWaitlist`) instead of being a no-op. REQUIREMENTS.md and DESIGN.md updated to match. |
| 08 | Review found that enrollment did not implement DESIGN.md's "promote the older eligible waiter before admitting a newcomer" rule; fixed, and one existing test that encoded the old behavior was rewritten. A stale-DbContext defect was also found and fixed. |
| 09 | First build failed (missing `using` in Program.cs). A passing test run from the stale binary was discarded. |
| 11 | Review found that drop promoted at most one waiter even when several seats were open, and a REQUIREMENTS.md contradiction about dropping an unknown learner (review only; no files changed). |
| 12 | DESIGN.md still described the old enroll/drop promotion; corrected. |
| After 13 | Developer answered the open items; a repeated drop now returns `409` instead of `200 NotEnrolled`. The enqueue timestamp is recorded and shown as `joinedAtUtc`, while waitlist order stays the monotonic sequence. |

---

## Interaction 01 — Requirements Review

Role: Senior .NET Developer
Phase: Requirements Analysis

### Prompt

- **Role:** Senior .NET developer reviewing a new feature requirement.
- **Context:** Complete the Course Waitlist assessment (see the requirements folder): enrollment, dropping a learner, and FIFO waitlist promotion for a single course offering. Stack: .NET 8, C#, ASP.NET Core Minimal API, EF Core, SQLite in-memory. Understand the requirements before implementing.
- **Task:**
  - Review the assessment and its acceptance criteria.
  - Identify the required functionality and edge cases.
  - Separate explicit requirements from assumptions.
  - Map the acceptance criteria to the functionality to build.
  - Create REQUIREMENTS.md with the findings.
- **Guardrails:** no implementation code yet; nothing outside the assessment; document any assumed business behavior; keep requirements clear and testable.
- **Expected output:** REQUIREMENTS.md covering scope, acceptance criteria, assumptions, and open questions; share findings before proceeding.

### AI Response

Extracted the text of the assessment PDF in `requirements/` (the workspace had no starter project) and created REQUIREMENTS.md with scope, explicit requirements, acceptance-criteria mapping, invariants, assumptions, and open questions. No code was written.

### Developer Decision

Accepted as a base. Superseded in part by Interaction 02, which supplied the business rules.

### Verification

Documentation only; no tests. The assessment PDF text was the source for the acceptance criteria.

---

## Interaction 02 — Business Rules

Role: Backend Developer
Phase: Requirements Refinement

### Prompt

- **Role:** Backend developer defining the enrollment business rules.
- **Context:** Initial requirements are in REQUIREMENTS.md. Agree how enrollment, waitlisting, prerequisite checks, and dropping should behave before implementation.
- **Task:** Update the requirements with these rules, and document state transitions and expected behavior for invalid requests:
  - One active registration per learner.
  - Eligible learners enroll when capacity is available; when full, they join the FIFO waitlist.
  - Learners failing prerequisites cannot enroll or newly join the waitlist.
  - When an enrolled learner drops, the earliest eligible waitlisted learner is promoted.
  - A waitlisted learner who becomes ineligible keeps their queue position but is skipped; if eligible again, they keep their original FIFO priority.
  - Duplicate enrollment requests create no additional records.
  - Repeated drops return a clear result without changing state.
  - New learners never bypass older eligible waitlisted learners.
- **Guardrails:** don't implement the service yet; keep the prerequisite rule simple; no configurable policy engine; highlight ambiguity instead of silently deciding; don't contradict the assessment.
- **Expected output:** updated REQUIREMENTS.md with finalized rules, state transitions, and assumptions needing approval.

### AI Response

Rewrote REQUIREMENTS.md with registration states, a simple pass/fail prerequisite rule, enrollment and FIFO-fairness rules, drop/promotion rules, invalid-request behavior, a state-transition table, and open questions. The prerequisite's source of truth, re-enrollment after a drop, and the HTTP status vocabulary were flagged as needing approval.

### Developer Decision

Accepted.

### Verification

Documentation only; no tests. Rules were cross-checked against the assessment's acceptance criteria.

---

## Interaction 03 — Technical Design

Role: Senior .NET Backend Engineer
Phase: Design

### Prompt

- **Role:** Senior .NET backend engineer designing a small, reliable service.
- **Context:** Business rules are in REQUIREMENTS.md; a technical design is needed before code. Stack: .NET 8, ASP.NET Core Minimal API, EF Core, SQLite in-memory.
- **Task:** Create DESIGN.md covering:
  - Project structure; domain entities and relationships; enrollment and waitlist states.
  - Service interfaces and method contracts; Minimal API endpoints.
  - Database constraints; FIFO ordering strategy; prerequisite validation.
  - SQLite in-memory connection management; transaction and concurrency handling; error handling and result types.
  - Explain how the design prevents over-enrollment and duplicate registrations, how the database stays available across requests, and how tests use isolated databases.
- **Guardrails:** keep the design proportional; no unnecessary layers; no authentication, frontend, external integrations, or deployment infrastructure; no EF Core InMemory provider; don't assume DbContext is thread-safe; no implementation code yet.
- **Expected output:** DESIGN.md with architecture, database model, service contracts, invariants, and technical decisions.

### AI Response

Created DESIGN.md. Main choices: a monotonic per-offering `NextWaitlistSequence` counter for FIFO ordering; one `Registration` row per learner and offering, reused after a drop; one singleton open `SqliteConnection` keeping the in-memory database alive; a singleton `SemaphoreSlim` gate plus a transaction around every operation; a boolean `Learner.PrerequisiteSatisfied`; typed result enums; and a proposed endpoint and status mapping.

### Developer Decision

Accepted. REQUIREMENTS.md and DESIGN.md were approved for implementation.

### Verification

Documentation only; no tests.

---

## Interaction 04 — Solution Scaffold

Role: .NET Backend Developer
Phase: Project Setup

### Prompt

- **Role:** .NET backend developer setting up the application.
- **Context:** REQUIREMENTS.md and DESIGN.md are approved; start implementing. Stack: .NET 8, ASP.NET Core Minimal API, EF Core SQLite, xUnit.
- **Task:**
  - Use the existing starter solution if available; otherwise create a minimal Web API project and a separate xUnit test project.
  - Add the required NuGet packages; set up dependency injection and configuration.
  - Create the folder structure from DESIGN.md; make sure the solution builds.
- **Guardrails:** no business logic yet; no unnecessary projects or packages; don't replace existing starter interfaces without reason; keep setup minimal and easy to run.
- **Expected output:** a compilable solution; run `dotnet restore` and `dotnet build` and share the actual results.

### AI Response

No starter existed and no .NET SDK was installed. Downloaded a temporary SDK, then created `CourseWaitlist.sln`, an API project (EF Core Sqlite 8.0.32) and an xUnit test project, plus `.gitignore` and empty folders.

### Developer Decision

Accepted.

### Verification

`dotnet restore` and `dotnet build` were run; both succeeded.

---

## Interaction 05 — Persistence

Role: .NET Developer (EF Core / SQLite)
Phase: Data Layer

### Prompt

- **Role:** .NET developer experienced with EF Core and SQLite.
- **Context:** Project structure is ready; persistence is needed for the course offering, enrollment records, and waitlist. SQLite in-memory means connection lifetime and consistency need attention.
- **Task:**
  - Implement the entities from DESIGN.md; configure the EF Core DbContext.
  - Add the relationships and unique constraints.
  - Implement deterministic FIFO ordering with a database-generated sequence or ID.
  - Keep the SQLite in-memory database alive while the app runs; give each test an isolated database.
  - Add focused tests for persistence and constraints.
- **Guardrails:** no EF Core InMemory provider; don't rely only on timestamps for FIFO; don't share a DbContext between concurrent operations; no enrollment or drop logic yet; avoid unnecessary repository abstractions.
- **Expected output:** working SQLite persistence, a successful build, and passing database tests.

### AI Response

Implemented the entities, `CourseWaitlistDbContext` (foreign keys, unique and check constraints), a `SqliteDatabase` helper, DI wiring, a `TestDatabase` helper giving each test a private open `:memory:` connection, `PersistenceTests`, and `ApplicationSetupTests`. Chose the plain `Data Source=:memory:` connection string because a shared-cache string would break test isolation.

### Developer Decision

Accepted. The connection-string choice (plain `:memory:`) was the assistant's own correction of a shared-cache approach, reported in the response.

### Verification

Build and test were run: 19 tests passed.

---

## Interaction 06 — Enrollment Service

Role: Backend Developer
Phase: Business Logic Implementation

### Prompt

- **Role:** Backend developer implementing the course enrollment service.
- **Context:** Database and entities are ready; enrollment must follow REQUIREMENTS.md and DESIGN.md. A learner is either enrolled or waitlisted depending on eligibility, capacity, and existing queue priority.
- **Task:** Implement the enrollment service. Flow:
  1. Validate the learner ID.
  2. Check whether the learner is already enrolled or waitlisted.
  3. Validate prerequisite eligibility.
  4. Check capacity and existing waitlist priority.
  5. Enroll if a seat is available and no older eligible learner has priority; otherwise add the eligible learner to the waitlist.
  6. Return a clear result for each outcome.
  - Add service-level tests for successful enrollment, full capacity, duplicates, and prerequisite failures.
- **Guardrails:** no business logic in API handlers; no duplicate registrations; never exceed capacity; don't bypass eligible waitlisted learners; follow the approved concurrency design; no dropping or promotion yet.
- **Expected output:** working enrollment service, focused tests, and actual build/test results.

### AI Response

Implemented `RegistrationService.EnrollAsync` (gate, transaction, validation, duplicate check, prerequisite check, capacity and queue logic), `IRegistrationService`, typed `EnrollmentOutcome`/`EnrollmentResult`, and `EnrollmentServiceTests`. The assistant flagged that DESIGN.md's "promote the older eligible waiter when a seat is open" step was not implemented here (a newcomer was simply waitlisted) and asked for a decision.

### Developer Decision

Accepted. The enroll-time promotion gap flagged here was closed in Interaction 08.

### Verification

Build and test were run: 42 tests passed.

---

## Interaction 07 — Drop and Promotion

Role: Backend Developer
Phase: Waitlist Lifecycle

### Prompt

- **Role:** Backend developer implementing the waitlist lifecycle.
- **Context:** Enrollment is implemented; now support dropping learners and automatic promotion from the waitlist. Approved policy: retain ineligible waitlisted learners but skip them during promotion.
- **Task:** Implement the drop operation.
  - When an enrolled learner drops: remove the enrollment, check seat availability, read waitlisted learners in FIFO order, recheck prerequisite eligibility, skip and retain ineligible learners, promote the earliest eligible learner, and commit the drop and promotion atomically.
  - When a waitlisted learner drops: remove their queue entry without affecting enrolled capacity.
  - Add tests for FIFO promotion, eligibility changes, skipped learners, invalid drops, and repeated drops.
- **Guardrails:** don't change the FIFO order of skipped learners; never promote an ineligible learner; no partially committed drop/promotion; don't change approved business rules; keep logic in the service layer.
- **Expected output:** working drop and promotion with passing automated tests.

### AI Response

Implemented `DropAsync` with outcomes `DroppedAndPromoted`, `DroppedSeatAvailable`, `DroppedFromWaitlist`, `NotEnrolled`, `OfferingNotFound`, `LearnerNotFound`, and `InvalidRequest`. Added a `TestDatabase.CreateContext(IInterceptor)` overload and `DropServiceTests`, including a rollback test that injects a failure after the changes are written, and a concurrent-drops test. Updated REQUIREMENTS.md and DESIGN.md because this prompt requires a waitlisted learner's drop to remove the queue entry, which replaced the earlier "no-op" rule.

### Developer Decision

Modified: the prompt itself changed the earlier rule (waitlisted drop removes the queue entry), and the assistant updated the docs to match.

### Verification

Build and test were run: 68 tests passed on 5 consecutive runs with 0 warnings.

---

## Interaction 08 — Concurrency Review

Role: Senior .NET Developer
Phase: Concurrency and Data Consistency

### Prompt

- **Role:** Senior .NET developer reviewing concurrency and data consistency.
- **Context:** Enrollment, dropping, and promotion are implemented. Ensure concurrent requests don't violate capacity, uniqueness, or FIFO behavior.
- **Task:** Review the implementation for race conditions and test these scenarios, using the transaction and synchronization approach from DESIGN.md:
  - Two learners enrolling when one seat is available.
  - Multiple requests for the same learner.
  - A drop and an enrollment happening concurrently.
  - Concurrent operations involving the waitlist.
  - Add focused concurrency tests and inspect the final database state after each.
- **Guardrails:** no DbContext shared across threads; don't assume constraints alone prevent all races; no distributed locking for a single-process scope; don't weaken tests to make them pass; document the approach's limitations.
- **Expected output:** concurrency-safe behavior for the documented single-process scope, with test results and remaining limitations.

### AI Response

Found two issues in its own earlier code: (1) a DbContext reused across calls could use a stale `Learner` or stale `NextWaitlistSequence`, fixed with `ChangeTracker.Clear()` after taking the gate; (2) enrollment did not implement the DESIGN.md open-seat promotion rule, now implemented (promote up to the open seats in FIFO order among eligible waiters, then enroll or queue the newcomer). Added `EnrollmentResult.PromotedLearnerIds`, `DatabaseInvariants.AssertConsistent`, and 19 concurrency tests. Rewrote the existing test `Newcomer_does_not_take_an_open_seat_ahead_of_an_older_eligible_waiter`, which had encoded the old behavior, and said so. Added a concurrency-review and limitations section to DESIGN.md.

### Developer Decision

Accepted. The rewritten test and the resulting behavior change were disclosed in the response.

### Verification

Build and test were run: 93 tests passed. The suite was repeated 6 more times with 0 failures.

---

## Interaction 09 — Minimal API Endpoints

Role: ASP.NET Core Backend Developer
Phase: API

### Prompt

- **Role:** ASP.NET Core backend developer exposing the existing service over HTTP.
- **Context:** Business logic is implemented and tested; a simple API is needed to exercise enrollment and dropping.
- **Task:** Implement Minimal API endpoints `POST /course/enroll`, `POST /course/drop`, and `GET /course/state` using the existing service methods. Return consistent JSON and appropriate status codes for enrollment, waitlisting, duplicates, invalid input, and drops. Run the app and test with curl.
- **Guardrails:** no business logic in Program.cs; no frontend; no authentication or unrelated endpoints; no unnecessary dependencies; keep contracts consistent with DESIGN.md.
- **Expected output:** working endpoints, example curl commands, and actual HTTP responses.

### AI Response

Added `CourseEndpoints.cs` (thin handlers that map typed service outcomes to HTTP), `IRegistrationService.GetStateAsync` plus `CourseState`, a config-driven single-course seed (`CourseSettings`, `CourseSeeder`, `appsettings.json`), a central exception handler returning a generic 500, and `CourseEndpointTests`. Updated the DESIGN.md endpoint table to the `/course/*` routes. The first build failed because Program.cs lacked a `using`; the 93 passes reported in that same command came from the stale binary and were not counted. Fixed, rebuilt, and ran the app.

### Developer Decision

Accepted. The status mapping was later confirmed by the developer, with repeated drops changed to `409` (see the final section).

### Verification

After the fix: build 0 warnings and 0 errors; 114 tests passed. The app was started and every outcome (enrolled, waitlisted, duplicate, prerequisite failure, unknown learner, invalid bodies, each drop outcome) was exercised with curl and returned the expected status and JSON.

---

## Interaction 10 — Test Coverage Review

Role: .NET Developer (Automated Testing)
Phase: Test Verification

### Prompt

- **Role:** .NET developer responsible for automated testing.
- **Context:** Service and API are implemented; verify the assessment's acceptance criteria are covered by automated tests.
- **Task:** Review the existing xUnit tests against REQUIREMENTS.md and the assessment. Ensure coverage of: available capacity, full capacity, duplicate enrollment, FIFO promotion, prerequisite failure, ineligible learners during promotion, invalid requests, repeated drops, and concurrent requests. Add missing tests using isolated SQLite in-memory databases, run the full suite, and review any failures.
- **Guardrails:** don't modify business rules to make tests pass; don't mock away database behavior; don't rely on test order; assert final enrollment and waitlist state, not just messages; report failures rather than hiding them.
- **Expected output:** passing xUnit suite with a clear mapping from acceptance criteria to tests.

### AI Response

Mapped the existing tests to the criteria and found gaps: enroll-time promotion with ineligible waiters, API-level skip-and-retain behavior, and concurrent HTTP requests. Added `AcceptanceGapTests.cs` (7 tests) and `TESTING.md` (criteria-to-test map and known gaps). No production code or rules were changed.

### Developer Decision

Accepted.

### Verification

Build and test were run: 121 tests passed; the suite was repeated 6 more times with 0 failures.

---

## Interaction 11 — Pre-submission Code Review

Role: Senior .NET Engineer
Phase: Review

### Prompt

- **Role:** Senior .NET engineer reviewing the completed implementation.
- **Context:** The application and automated tests are in place; review against the original assessment, REQUIREMENTS.md, and DESIGN.md before submission. Confirmed issues will be fixed and tests rerun after review.
- **Task:** Review the code for missing requirements, incorrect state transitions, capacity violations, FIFO ordering issues, prerequisite validation problems, duplicate registration risks, concurrency issues, SQLite connection lifetime problems, and unnecessary complexity. Identify issues and explain their impact.
- **Guardrails:** don't assume AI-generated code is correct; no code changes during the initial review; no unrelated refactoring; don't change specifications to justify the implementation; distinguish confirmed defects from potential risks.
- **Expected output:** prioritized findings with file references, affected requirements, and recommended fixes.

### AI Response

Reviewed the code and docs and re-read the assessment PDF. No files were changed. Confirmed findings: (1) the AI-use record, README, and limitations summary deliverables were missing; (2) drop promoted at most one waiter even when several seats were open, a FIFO/utilization gap with a reachable scenario; (3) REQUIREMENTS.md contradicted itself on dropping an unknown learner; (4) DESIGN.md had drifted from the code. Listed unconfirmed risks (client-cancel logging, promotion only on enroll/drop, no identifier normalization, enrolled-list ordering, no gate timeout) and areas found sound (capacity, duplicates, FIFO, atomicity, connection lifetime).

### Developer Decision

Accepted.

### Verification

Review only; no code changed and no tests were run in this interaction. The last test result at that time was 121 passing (Interaction 10). The "no over-enrollment path" and other "found sound" conclusions came from reading the code and from earlier test runs, not from new execution.

---

## Interaction 12 — Final Verification

Role: .NET Developer
Phase: Submission Readiness

### Prompt

- **Role:** .NET developer preparing the completed feature for submission.
- **Context:** The implementation has been reviewed and confirmed issues addressed; verify it builds, runs, and satisfies the assessment.
- **Task:** Run `dotnet restore`, `dotnet build`, and the complete xUnit suite; start the app and test the main API scenarios with curl, verifying enrollment and waitlist state after each operation; check that README.md has correct setup and test commands; confirm DESIGN.md matches the implementation; list remaining limitations.
- **Guardrails:** no new features; no unnecessary code changes; don't report tests as passed unless executed; don't claim concurrency guarantees beyond the actual scope; don't invent verification evidence.
- **Expected output:** final build and test results, working API verification, updated setup instructions, and a concise limitations summary.

### AI Response

Ran restore, a non-incremental build, and the full suite; started the app with the README command and exercised 17 curl scenarios with `GET /course/state` after each step; ran two parallel curl bursts. Found DESIGN.md still described single-waiter promotion on enroll and said the AI record was still to be written, and corrected only those parts. Updated the test count and added the two new tests in TESTING.md. No code was changed. Listed the remaining limitations.

### Developer Decision

Accepted.

### Verification

- `dotnet restore`: succeeded.
- `dotnet build --no-incremental`: 0 warnings, 0 errors.
- `dotnet test`: 123 passed, 0 failed, 0 skipped.
- App run with curl: all 17 scenarios returned the expected status codes, and the state after each matched. A parallel burst (6 enroll requests for bob/dave on a full course: 2 successes and 4 conflicts, queue positions 3 and 4; 4 drops of alice/carol, all 200) ended in consistent state with no over-enrollment.
- Skip-an-ineligible-waiter behavior cannot be shown through the API (no endpoint changes eligibility) and was covered only by automated tests.

---

## Open items and developer decisions

All five items raised during the work have been answered by the developer:

1. Prerequisite representation (`Learner.PrerequisiteSatisfied`, seeded from `appsettings.json`): approved.
2. Re-enrollment after a drop: follows the assessment and REQUIREMENTS.md; a dropped learner submits a fresh request, subject to current eligibility and FIFO rules.
3. HTTP status mapping: modified. A repeated drop returns `409` (code `NotEnrolled`) and an unknown learner's drop returns `404`. This replaced the earlier `200 NotEnrolled`. Code, tests, README.md, DESIGN.md, REQUIREMENTS.md and TESTING.md were updated. Build: 0 warnings, 0 errors; 123 tests passed; a live curl check showed repeated drop `409`, unknown learner `404`, and state unchanged.
4. Enroll-time promotion of older eligible waiters when a seat is open: approved as implemented.
5. Single-process concurrency scope: approved as implemented.

Questions 2, 5 and 6 in REQUIREMENTS.md were also answered by the developer:

6. FIFO ordering key: the assistant flagged that ordering by timestamp first adds nothing over the sequence and that a backwards clock change could reorder the queue. The developer chose to keep the sequence as the order and record the enqueue timestamp for display. Implemented with `Registration.EnqueuedAtTicks`, `joinedAtUtc` in `/course/state`, and an optional `TimeProvider` on `RegistrationService`; 5 tests in `WaitlistOrderingTests` (sequence decides order even when timestamps disagree or the clock moves backwards). Build: 0 warnings, 0 errors; 128 tests passed, repeated 5 more times with 0 failures; a live curl check showed `joinedAtUtc` in the waitlist and FIFO promotion unchanged.
7. Identifier validation: approved as implemented (exact match; missing, blank, or non-string identifiers are invalid; unknown learner is `404`).
8. Transaction/locking conventions: approved as implemented (in-process gate plus transaction).
