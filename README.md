# Course Waitlist

Enrollment, drop, and FIFO waitlist promotion for a single course offering. .NET 8, ASP.NET Core Minimal API, EF Core, SQLite in-memory, xUnit.

Supporting documents: [REQUIREMENTS.md](REQUIREMENTS.md) (rules and assumptions), [DESIGN.md](DESIGN.md) (architecture, concurrency), [TESTING.md](TESTING.md) (acceptance criteria to tests), [AI_USAGE.md](AI_USAGE.md).

## Prerequisites

.NET 8 SDK (`dotnet --version` should report 8.x).

## Build, test, run

```bash
dotnet restore CourseWaitlist.sln
dotnet build CourseWaitlist.sln --no-restore
dotnet test CourseWaitlist.sln --no-build        # 128 tests
dotnet run --project src/CourseWaitlist.Api --urls http://127.0.0.1:5088
```

In Development, Swagger UI is available at `http://127.0.0.1:5088/swagger`.

The database is SQLite in-memory: it lives as long as the running process and is empty (apart from the seed) on each start.

## Seed data

`appsettings.json` (`Course` section) defines the one offering (`course-101`, capacity 2) and known learners: `alice`, `bob`, `carol`, `dave` (prerequisite met) and `erin` (prerequisite not met). There are no endpoints to create learners or change eligibility.

## API

All bodies are JSON; errors are `{ "code", "message" }`.

```bash
B=http://127.0.0.1:5088/course
curl -X POST $B/enroll -H 'Content-Type: application/json' -d '{"learnerId":"alice"}'
curl -X POST $B/drop   -H 'Content-Type: application/json' -d '{"learnerId":"alice"}'
curl $B/state
```

| Request | Result |
|---|---|
| `POST /course/enroll` | `200` `Enrolled` or `Waitlisted`; `409` `AlreadyRegistered`; `422` `PrerequisiteNotMet`; `400` `InvalidRequest`; `404` `LearnerNotFound` |
| `POST /course/drop` | `200` `DroppedAndPromoted`, `DroppedSeatAvailable`, or `DroppedFromWaitlist`; `409` `NotEnrolled` (repeated drop, no change); `400`; `404` `LearnerNotFound` |
| `GET /course/state` | `200` capacity, enrolled learners, waitlist with positions |

## Key assumptions

(Full list and open approvals in REQUIREMENTS.md and DESIGN.md.)

- Prerequisite is a simple pass/fail flag per learner. A learner who fails it cannot enroll or join the waitlist.
- Ineligible waitlisted learners are skipped during promotion but keep their queue position and original priority.
- FIFO order is a monotonic per-offering sequence, not timestamps. The enqueue time is recorded and shown as `joinedAtUtc` in `/course/state` for information only, so a clock change cannot reorder the queue.
- A dropped learner may enroll again and is treated as a new request (back of the queue if full).
- When a seat is open, older eligible waiters are promoted before a newcomer is admitted.

## Limitations and follow-up risks

- **Single process only.** Concurrency safety comes from a process-wide async gate plus a database transaction. Multiple instances, or anything else touching the database, are not protected. The SQLite in-memory database is process-local anyway.
- **All database access must go through the service.** A context used outside the gate on the shared connection could join an open transaction.
- **Serialized operations.** One request at a time, which is fine for one course but limits throughput. The gate has no timeout, so a hung database call would block all requests.
- **Promotion only happens on enroll or drop.** A waiter who regains eligibility while a seat is open is promoted on the next enroll or drop, not immediately. There is no API to change eligibility, so the skip behavior is demonstrated through tests.
- **No retry policy.** An unexpected database error rolls back and returns a generic 500.
- **Identifiers are matched exactly** (no trimming or case folding).
- **Concurrency tests** repeat contended scenarios and cannot prove the absence of races.
- **Cancellation handling** (client disconnect logged at information level, status 499) is not covered by an automated test.
- **Confirmed assumptions:** the prerequisite representation, re-enrollment after a drop, the HTTP status mapping (`409` `NotEnrolled` for repeated drops, `404` for an unknown learner's drop), enroll-time promotion, and the single-process scope.
