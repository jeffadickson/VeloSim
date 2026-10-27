# Dynamic array allocation through the HDL pipeline

The bounded feature accepts a module-scope one-dimensional dynamic array of
integral elements, procedural `m = new[count]`, self-preserving
`m = new[count](m)`, blocking indexed writes, indexed reads, `m.size()` and
`m.delete()`. Elements use the existing signedness, two/four-state and1..256-bit
word rules. A dynamic array begins empty. Fresh cells initialize to the element
default, X for four-state integral words and zero for two-state words.

Allocation counts are sampled once. Counts must be known and nonnegative and
cannot exceed256cells; this is a checked simulator resource limit. Zero-sized
allocation and deletion produce empty storage. Self-preserving resize retains the
common prefix, initializes a grown tail and discards a truncated tail. Invalid or
unknown indices yield the element default and ignore writes. Address expressions
retain the existing explicit unsigned-index contract.

The Surelog/UHDM exporter identifies typed array declarations, allocation argument
lists and method receivers. It emits strict `dynamic` memory metadata and
`allocate`, `delete`, `size` IR nodes. Dynamic memory uses depth1 metadata for
backward compatibility with fixed-memory declaration validation; that value is
never its initial allocation size. Both standalone native models and external
SDKs begin with zero cells and emit the same operations. SDK evaluation snapshots
copy the vectors and restore them, signals and edge history on failure. Callback
side effects and queued external drives remain outside rollback.

Lifetime mutations and element writes must use blocking assignments in one
isolated sequential process. Other processes and settled combinational logic may
not observe storage that process mutates. Startup remains the existing single
untimed initialization phase. Queue/associative methods, cross-array initializer
copies, whole-array assignment, declaration initializers, multidimensional dynamic
arrays, reference arguments, lifetime operations inside pure functions and general
timed scheduling remain unsupported. Unsupported shapes fail closed.

See [the runnable HDL example](../examples/dynamic_array.sv). The external word
ABI stays2; previously built ABI1C++ hosts require rebuilding. This feature does
not change the independent64-bit IR scheduler.

Verification records, including commands, exact source hashes, independent
expected traces, original HDL reference execution, standalone/SDK outputs,
negative tests, seeded properties, coverage and mutation findings, are retained
under `artifacts/dynamic-array` and the Linux-side fixture directories named by
the run. Qualification status and metrics are recorded in the continuation prompt
after checks complete; do not treat an implementation description as a verified
requirement closure.

Current qualification:21 focused tests pass with zero failures/skips;59 selected
IR/process regressions and12 HDL/rejection regressions pass in separate runs.
Native CTest8/8 and ASan/UBSan7/7 pass. Four compiled native allocation faults are
detected after a passing control; compile failures/crashes without the assertion
marker are not counted as semantic detections. Seeded FsCheck depth/schema tests
use seed(20261101,864013), with additional existing malformed-input seeds recorded
in the selected regression logs. The HDL/SDK probe verifies recovery after a
prefix-preserving truncation followed by a rejected allocation, including a retry
of the same clock edge and preservation of the truncated cell.

Focused merged managed coverage for RtlDesign.cs/RtlArrays.cs is259/298lines
(86.9% displayed) and632/1023branches(61.7% displayed). Native runtime.h coverage
is161/166lines96.99% and410/446branches91.93%. Focused exporter coverage for
rtl.cpp/rtl_flatten.h is177/204lines86.76% and419/1052branches39.83%, from15profiles.
These scopes differ from earlier full-suite reports and are not directly comparable.
The fresh focused LLVM exports report no diagnostics; the prior full-suite LLVM
mapping limitation is not declared resolved. Untested branches remain visible.

The first narrow Stryker.NET5.0.0 run on RtlArrays.cs tested53mutants:31killed,
22survived, with8additional no-coverage mutants, yielding50.82% reported score.
Compilation/instrumentation failures and mutate-filter exclusions were not kills.
Subsequent schema tests strengthen the selected test set; later worker reports
retain their own source snapshots and scores rather than rewriting this history.
Error-message-only mutations and gaps in fixed-array indexing were among the
survivors. This is neither exhaustive mutation coverage nor a correctness proof.

Icarus returns X for invalid two-state dynamic-array reads in this environment.
The two-state HDL comparison therefore uses an explicit bounds guard, while direct
native tests independently verify default-zero invalid reads. Direct HDL evidence
covers packed logic/bit words at8and256bits; other integral forms retain existing
exporter restrictions and are not newly qualified by those three HDL cases.

The later isolated worker smoke ran60selected managed tests successfully and
completed all four native domains. Its strengthened narrow Stryker test set
reported48killed,9survived and4no-coverage mutants(78.69% from these counts).
1540compile/instrumentation errors and10829filtered/ignored mutants were excluded
from detections. All three generated dynamic SDKs also pass ASan/UBSan and match
their original-HDL reference traces, including the failure-recovery probes.

The [array mutation follow-up](array-mutation-review.md) investigates that worker
report's surviving and uncovered cases, with new public-IR regression tests and
an explicit disposition for the parser-masked depth mutation.
