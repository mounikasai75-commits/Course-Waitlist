# Course Waitlist Requirements

## Purpose and scope

Implement one course offering's enrollment, learner drop, and FIFO waitlist-promotion workflow. This is a bounded service slice, not a complete registration product.

The workspace currently contains only the assessment PDF and no starter project. The implementation context supplied for this work is .NET 8, C#, ASP.NET Core Minimal API, EF Core, and SQLite in-memory. Keep the design proportionate to that single-offering workflow.

### In scope

- Enroll a learner when a seat is available and the learner meets the defined prerequisite rule.
- Add an eligible learner to the waitlist when the offering is full.
- Prevent duplicate enrollment/waitlist records for repeated enrollment requests.
- Drop an enrolled learner and, if a seat opens, promote an eligible waitlisted learner using FIFO order.
- Return clear results for invalid requests and duplicate drops without corrupting state.
- Focused automated tests for the acceptance criteria.
- Document the service boundary, relevant data/API design, invariants, promotion and concurrency/persistence approach, assumptions, tradeoffs, setup/test commands, AI use, and in-scope limitations or follow-up risks.

### Out of scope

Unless the starter project is later found to require them, do not add a frontend, course catalog, payments, identity integrations, multi-term registration, deployment, a new database, or a configurable policy engine. Do not implement separate greenfield, brownfield, and ambiguous scenarios.

## Explicit requirements

The following are stated by the assessment, implementation context, or business rules supplied for this update:

1. The workflow applies to one course offering.
2. A learner can have only one active registration for the offering. Enrolled and waitlisted are active states.
3. An eligible learner enrolls when capacity is available, provided this does not bypass an older eligible waitlisted learner.
4. When full, an eligible learner may join the FIFO waitlist once.
5. Learners who fail prerequisites cannot enroll or newly join the waitlist.
6. Dropping an enrolled learner frees a seat and promotes the earliest eligible waitlisted learner.
7. A waitlisted learner who becomes ineligible remains in their original queue position but is skipped during promotion. If eligible again, they retain that original priority.
8. Duplicate enrollment requests must not create additional records.
9. Repeated drops return a clear result without changing state.
10. New learners must not bypass older eligible waitlisted learners.
11. Invalid requests and duplicate drops must not corrupt registration, capacity, or queue state.
12. Tests cover available/full capacity, duplicate requests, FIFO promotion, prerequisite failure, ineligible waitlist retention and skip behavior, invalid requests, and invalid/repeated drops.
13. If the starter uses concurrent requests, preserve its transaction or locking pattern and add a focused concurrency test where practical.
14. At least one engineering activity uses AI. Record prompts, relevant response excerpts or summaries, whether suggestions were accepted/edited/rejected, files or decisions informed, and verification evidence.
15. Deliver runnable scoped code and tests, a proportionate design specification, an AI prompt/use record, setup/test instructions, and a concise summary of in-scope limitations or follow-up risks.

## Finalized business rules

These rules define the scoped workflow; they do not call for a configurable eligibility or waitlist policy engine.

### Registration states

- **Not registered:** no active registration exists for the offering.
- **Enrolled:** the learner occupies one seat.
- **Waitlisted:** the learner holds one FIFO queue position and occupies no seat.
- **Dropped:** the prior active registration has ended; the learner is no longer enrolled or waitlisted.

An active registration is exactly one of Enrolled or Waitlisted. A learner cannot hold multiple active registrations or be enrolled and waitlisted at the same time.

### Prerequisite check

Use a simple pass/fail prerequisite check. A failed check prevents a learner from newly enrolling or joining the waitlist. Check a waitlisted learner's current eligibility when considering them for promotion. If a learner who is already waitlisted fails the check, retain their queue position but skip them for that promotion. If they pass again, their original FIFO priority remains.

The assessment does not specify the prerequisite itself or its source of truth; the simplest representation (a boolean on the learner) was approved by the developer (see Resolved questions).

### Enrollment and FIFO fairness

1. Reject invalid requests without mutating registration or queue state.
2. If the learner already has an active registration, return a clear duplicate/already-registered result without creating or reordering anything.
3. Do not admit a learner who fails the prerequisite check to either active state.
4. Enroll an eligible learner when a seat is available and no older eligible learner is waitlisted.
5. If the offering is full, add an eligible new learner to the tail of the waitlist.
6. If a seat is available and an older eligible learner is waitlisted, the older learner has priority: promote the earliest eligible queued learner and put the new learner at the queue tail. The new learner must not take that seat ahead of the older eligible learner.
7. Older currently-ineligible waitlisted learners retain their positions but are skipped. If no older eligible waiter exists and a seat is available, an eligible new learner may take the seat. If the offering is full, the new learner joins the tail.
8. Promotion scans queue order and selects the first currently eligible learner. Ineligible learners are neither removed nor reordered. If they later become eligible, they retain their original priority for a future seat.
9. FIFO ordering must be stable and deterministic; its concrete ordering key and tie-breaker are implementation decisions requiring documentation.

### Drop and promotion

1. A valid drop of an enrolled learner ends that registration and opens one seat.
2. Process the opened seat by promoting the earliest currently eligible waitlisted learner, skipping but retaining ineligible entries.
3. Promotion removes the selected learner from the waitlist and enrolls them as one atomic state transition.
4. If no waitlisted learner is eligible, leave the seat available and retain the existing queue order.
5. A waitlisted learner who drops leaves the queue (registration becomes Dropped) without affecting enrolled capacity, other waiters' positions, or promotion. Dropping someone with no active registration, including a repeated drop, returns a clear no-op result with no state change.

The drop, seat release, and any promotion must not leave partially applied state.

### Invalid requests and duplicate operations

- Malformed requests, invalid required identifiers, and unknown learner identifiers are rejected with a clear result and no state mutation.
- A valid enrollment request for a learner who is already enrolled or waitlisted returns a clear duplicate/already-registered result; it does not create another record or change queue order.
- A drop for a learner who exists but has no active registration, or a repeated drop, returns a clear not-enrolled result (`NotEnrolled`, HTTP `409`); it does not change capacity or the queue. A drop for an unknown learner identifier is rejected as `LearnerNotFound` (a distinct non-success result, see the first bullet), not treated as a no-op.
- Exact HTTP status codes, payload shapes, identifier format, and error vocabulary are API design choices not specified by the assessment. Outcomes must distinguish actual transitions from rejection/no-op results.

## Acceptance criteria mapped to behavior and verification

| AC | Required behavior | Minimum verification |
|---|---|---|
| 1 | With an available seat, an eligible learner becomes enrolled unless an older eligible waiter has priority. | Test available capacity, including a queued older eligible learner; verify no bypass and valid enrollment/queue state. |
| 2 | When full, an eligible learner joins the waitlist once. Repeated requests do not create duplicates or reorder the queue. | Test full-capacity waitlisting and repeated requests; verify one active registration and stable queue position. |
| 3 | Dropping an enrolled learner releases a seat and promotes the earliest currently eligible waitlisted learner. | Queue multiple learners, including an ineligible entry; drop an enrolled learner and verify FIFO eligible promotion, retained skipped position, and state/capacity. |
| 4 | Prerequisite failure prevents a new enrollment or waitlist entry. A previously waitlisted learner who becomes ineligible remains queued and is skipped; regained eligibility preserves priority. | Test failed admission, skip-with-retention during promotion, and restored eligibility on a later vacancy. |
| 5 | Invalid requests and invalid/repeated drops return clear results without corrupting state. | Test malformed/unknown identifiers, a learner not enrolled, and repeated drop; verify result and unchanged state. |
| 6 | Required cases are covered; existing concurrency/persistence patterns are preserved where applicable. | Run focused automated tests. If a supplied starter has concurrency handling, test a relevant concurrent operation where practical and preserve its transaction/locking pattern. |
| 7 | AI was used for an engineering activity and independently reviewed and verified. | Include prompt/use record with suggestion outcomes, affected files/decisions, and verification evidence. |

## Invariants to preserve

These invariants should be asserted in tests:

- The number of enrolled learners does not exceed offering capacity.
- A learner has at most one active registration: enrolled or waitlisted, never both.
- A temporarily ineligible waitlisted learner retains the same FIFO position.
- An ineligible learner is never enrolled or promoted.
- A successful drop applies only to an enrolled learner and opens one seat.
- Promotion fills an opened seat only with the earliest currently eligible waitlisted learner, without bypassing older eligible learners.
- If no waiter is eligible, the opened seat remains available.
- Failed, invalid, duplicate, or otherwise rejected operations do not leave partial state changes.

Exact response/status vocabulary is not prescribed. Outcomes must clearly distinguish successful transitions, duplicates, prerequisite failures, invalid requests, and no-op drops.

## Assumptions and decisions required

The business rules supplied for this update settle the active-registration, prerequisite-failure, FIFO-retention, skip, and drop behaviors. Remaining details that require approval or an explicit implementation assumption are:

| Topic | What is established | Decision still needed |
|---|---|---|
| Prerequisite representation | Use a simple pass/fail check; ineligible learners cannot newly enroll or waitlist. | Confirm the minimal data/source used to determine pass/fail. Do not infer a specific real-world prerequisite or build a policy engine. |
| FIFO ordering | Ineligible queue entries retain their original priority while skipped. | Ordering key: a monotonically assigned queue sequence; the enqueue timestamp is recorded for display only (approved). |
| New enrollment while an older waiter exists and a seat is open | Older eligible waiters cannot be bypassed; eligible newcomers go behind them. Older ineligible entries are skipped. | Confirm response semantics when processing the request promotes an older learner and the requesting learner ends up waitlisted. |
| Re-enrollment after drop | Dropped means no active registration; a learner can have only one active registration. | This document assumes a previously dropped learner may submit a fresh request, subject to current eligibility and FIFO rules. Confirm. |
| Duplicate enrollment result | Duplicate requests do not create records or reorder the queue. | Choose exact HTTP status/result shape for already-enrolled and already-waitlisted requests. |
| Invalid requests and unknown learners | Invalid requests are no-ops; unknown learner IDs are treated as invalid. | Define required fields, accepted identifier format, and exact status/payload. |
| Non-enrolled drop result | Unknown, not-enrolled, and repeated drops are clear no-ops. | Choose exact HTTP status/result shape. |
| Persistence/concurrency | The implementation context is EF Core with SQLite in-memory; preserve starter conventions if present. | The current workspace contains no starter files. Choose an atomic persistence/transaction approach; do not claim guarantees beyond implementation and tests. |

For any unspecified behavior, record the chosen assumption in the design specification and cover behavior that could affect state with a focused test.

## State transitions

| Current state | Request or event | Condition | Result |
|---|---|---|---|
| Not registered | Enroll | Invalid request or unknown learner | Reject; no registration, queue, or capacity mutation. |
| Not registered | Enroll | Prerequisite fails | Reject; remain Not registered; do not waitlist. |
| Not registered | Enroll | Eligible; seat available; no older eligible waiter | Enrolled. |
| Not registered | Enroll | Eligible; course full | Waitlisted at the queue tail. |
| Not registered | Enroll | Eligible; seat available; older eligible waiter exists | New learner goes to queue tail; earliest eligible waiter is promoted into the available seat. |
| Enrolled | Enroll | Repeated request | Remain Enrolled; return a clear duplicate/already-registered result. |
| Waitlisted | Enroll | Repeated request | Remain Waitlisted in original position; return a clear duplicate/already-registered result. |
| Enrolled | Drop | Valid drop | End registration; release seat; atomically promote earliest eligible waiter or leave seat available if none is eligible. |
| Waitlisted | Drop | Drop requested | Dropped; leave the queue without affecting capacity or other waiters' positions; no promotion. |
| Not registered or Dropped | Drop | Drop requested | Remain without active registration; return not-enrolled/no-op result. |
| Waitlisted | Eligibility becomes false | Not currently being promoted | Remain in same position; skip while ineligible. |
| Waitlisted | Considered for promotion | Eligible now | Atomically transition to Enrolled and remove from waitlist. |
| Waitlisted | Considered for promotion | Ineligible now | Remain in same position; continue scanning for the next eligible learner. |
| Dropped | Enroll | New valid request | Treat as a fresh request under current prerequisite, capacity, and FIFO rules. |

## Resolved questions

1. Prerequisite value and data source: a boolean `Learner.PrerequisiteSatisfied`, seeded from configuration (approved).
2. FIFO ordering: monotonic sequence decides order; the enqueue timestamp is recorded and shown as "joined at" but never used for ordering (approved).
3. A previously dropped learner may make a fresh enrollment request (approved).
4. HTTP mapping: `409` for duplicate enrollment, `422` for prerequisite failure, `400` for invalid requests, `409` `NotEnrolled` for a repeated/no-op drop, `404` for unknown learner or offering (approved).
5. Identifier validation: exact match; missing, blank, or non-string identifiers are rejected as invalid; an unknown learner is `404` (approved).
6. Transaction/locking conventions: no starter conventions exist, so the in-process gate plus transaction from DESIGN.md is used (approved).

## Verification and deliverables checklist

- [ ] Focused automated tests cover every scenario in AC 6 and the documented eligibility policy.
- [ ] The simple prerequisite check, skip-but-retain behavior, and eligibility re-check at promotion are applied consistently.
- [ ] A temporarily ineligible waitlisted learner retains original FIFO priority if eligibility returns.
- [ ] A new eligible learner never bypasses an older eligible waitlisted learner.
- [ ] State invariants hold after successful and rejected operations.
- [ ] Persistence/concurrency behavior is described accurately and tested where applicable.
- [ ] Setup and test commands are documented and run successfully.
- [ ] AI prompts/contribution, acceptance decisions, affected files or decisions, and independent verification are recorded.
- [ ] In-scope limitations and follow-up risks are summarized.
