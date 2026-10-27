# Bounded HDL queues

Module-scope one-dimensional queues of supported integral words now lower through
Surelog/UHDM into owning C++ storage. The supported operations are `push_back`,
`push_front`, `pop_back`, `pop_front`, `size`, `delete()` and `delete(index)`, plus
indexed reads and blocking writes. See [queue_demo.sv](../examples/queue_demo.sv).

Queues start empty. Four-state empty pops and invalid reads return X; two-state
elements return zero. Pushes and writes preserve element width/state, with signed
conversion at assignments. A write at exactly `size()` appends; unknown indices
and writes farther past the end are ignored. Invalid indexed deletion is ignored.
Indices follow the existing unsigned-address contract: signed expressions need
an explicit `$unsigned` cast, for example `pending[$unsigned(pending.size())]`.
Both push directions and append writes check a 256-cell resource ceiling before
changing storage. This is not bounded-queue `[$:N]` truncation.

Pop results must be assigned with a blocking assignment to a whole writable word,
or discarded as a standalone method statement. Pop calls nested inside arithmetic,
conditions or other expressions are rejected so operand evaluation order cannot
silently change the result. Element NBAs, combinational mutation, cross-process
access to mutated queues, queue ports, aggregate/nested queues, queue assignment,
queue literals, `$`-relative indexing, `insert`, sorting and iteration methods
remain outside this slice. Bounded declarations and associative arrays remain
unsupported. General timed scheduling remains open.

## Implementation

The IR adds optional memory metadata `queue:true`, mutually exclusive with
`dynamic:true`; depth1 is metadata for initially empty storage. `queue_push` and
`queue_pop` statements keep side effects explicit, and `delete` accepts an optional
index only for queues. `size` works for both dynamic arrays and queues. Queue pops
record both the queue and result word as blocking destinations in ownership checks.

The frontend binds methods to typed UHDM declarations, rather than matching dotted
source text. Both C++ emitters reuse existing word operations, vector snapshots,
startup handling and process-local blocking state. New vector adapters provide
queue operations without changing the SDK word ABI (still 2) or adding libraries.
An SDK evaluation failure restores the queue, outputs and clock history through
the existing transaction mechanism. Standalone command-line execution exits on
a resource error; it has no host retry API.

We inspected the pinned UHDM headers and actual elaborated queue objects. The
[Verilator queue implementation](https://github.com/verilator/verilator/blob/master/include/verilated_types.h)
also distinguishes queue append writes from dynamic-array writes and implements
empty-pop defaults; it was consulted as a primary implementation reference, not
added as a dependency. Existing `std::vector` storage was preferable here because
it preserves the simulator's single memory inventory and rollback snapshots.
With at most 256 cells, front insertion/removal has bounded linear work; no
performance improvement is claimed.

## Focused qualification

This change intentionally runs only queue checks and a few directly affected
regressions, not the full simulator suite. `VS-RTL-QUEUE-001` remains Draft pending
broader acceptance. The tracked qualification record and handoff describe actual
results, scope, seeds and limitations; earlier full-suite results are historical.

The focused selection executed 30 tests: 29 passed and one exposed Icarus's
empty `bit`-queue pop returning X. After adding an explicit empty guard to that
comparison's shared original HDL, only the affected case was rerun and passed.
No simulator behavior was changed to match the reference limitation. The
accepted evidence comprises 30 distinct passing cases across those runs,
including three original-HDL comparisons (22 rows each), four unsupported-HDL
rejections, queue IR guards and six directly affected regressions. Native tests
independently check the unguarded two-state empty-pop default of zero.

The three models match independent expected histories, standalone evaluation
with worker settings 1/4/8 and C++ SDK execution. SDK probes reject a push after
a pop and partial fill exceeds capacity, then retry the same edge and check
size/content recovery. Worker settings still use serial RTL evaluation; this
does not establish a parallel simulation speedup.

| Measurement | Focused result | Scope |
| --- | --- | --- |
| Coverlet lines / branches | 16/16 (100%) / 12/12 (100%) | New `RtlQueues.cs`, including its iterator mapping, initial focused run |
| LLVM lines / branches | 17/18 (94.44%) / 25/30 (83.33%) | Four new native queue adapter functions, direct native test only |
| LLVM lines / branches | 197/228 (86.40%) / 436/1124 (38.79%) | Whole `rtl.cpp` and `rtl_flatten.h`, eight focused exporter profiles |
| Native / ASan+UBSan | 1/1 / 1/1 | Queue target only; leak checks enabled in sanitizer run |
| Compiled semantic faults | Control passes; 4/4 detected | Pop direction, capacity, append publication and two-state coercion |

The native mutation probe is C# compiled with NativeAOT and run through the
incremental `make queue-mutation-probe JOBS=4` target. It compiles isolated C++
copies and requires an assertion marker; compilation failures and incidental
crashes do not count as detections. This is four targeted faults, not a general
mutation score. The existing dynamic-mode default-read target was made unique
after adding the queue default path; older modes were not rerun. Stryker.NET,
the full native/managed/HDL suites and long campaigns were not run for this
limited check. Helper instruction coverage and sanitized generated SDK coverage
remain unmeasured. The queue metadata FsCheck property ran 100 cases with seed
`(20261102,864013)`.

`examples/queue_demo.sv` also compiles with Icarus and elaborates/exports through
Surelog/UHDM. That example check is compilation/export evidence, not an additional
behavioral simulation. Raw evidence remains in the native home workspace and
ignored `artifacts/queue-feature`; see `prompt.md` and the tracked qualification
record for source/report bindings and reproduction commands.
