# Review: shared-clock processes, hierarchy and signed 128-bit words

The reviewed scope is the three priorities at the end of the preceding assessment: shared-clock processes, bounded hierarchy and signed/wider arithmetic. General multi-clock event scheduling and composite SystemVerilog types remain outside this change. The execution contract is in [the milestone guide](../rtl-processes-hierarchy-signed.md).

An independent AI peer reviewed the process snapshot/commit rules, global driver checks, hierarchy port bridges, declaration identity, signed expression/context propagation, native arithmetic and VPI value handling. This was not a human expert review. The reviewer made no edits and ran no builds; the implementation agent ran the verification.

Review findings fixed before commit:

- Unresolved non-null `vpiActual` chains now fail instead of falling back to a same-named declaration in the current scope.
- Generated scopes without `vpiName` recover their name from the elaborated full name, retaining paths such as `g.u0.q`.
- Signed index/base expressions fail validation, except nonnegative signed constants. `$unsigned` provides an explicit conversion. This prevents a negative small signed index from silently addressing a positive array element.
- VPI signal metadata preserves signedness. Decimal reads and callback payloads use unsigned magnitudes to format signed values safely, including MIN128.
- Parameter constants use each elaborated instance's assignments and preserve declaration signedness. Unsized decimal literals retain signed semantics despite UHDM's unsigned value representation.
- Retained UHDM dumps showed a pre-folded literal shift with different self-determined sizing. Exact original source spans now recover the original expression tree. Missing matches retain the elaborated constant, and ambiguous matches reject; recovery depends on frontend metadata.
- A native checked 128-bit constant decoder replaces UHDM's 64-bit numeric conversion for decimal and based literals, including octal and X/Z padding.
- Full regression exposed a calculated replication count folded without its two-bit wrap. Fixed bounds/counts now recover original syntax before requiring literals; the targeted rejection regression passed after rebuilding the exporter.

The reviewer found no additional concrete semantic blocker after these fixes, with final acceptance conditional on the rebuilt-exporter comparisons. Tests deliberately retain unsupported width/connection/driver cases; prior 64-bit rejection boundaries move to 129 bits where the supported range expanded. Tests for signed memories, negative parameters and generated scopes move into positive same-source qualification.

Coverage uses the existing managed and native kernel/RTL/VPI gates. The UHDM exporter is exercised by same-source conformance tests; it is not included in the current LLVM line-coverage object list. The full managed conformance timeout is 45 minutes to allow per-fixture native SDK builds; other build commands retain the 15-minute limit.

Final verification after the replication-count fix: 317 regular managed/HDL tests passed with `VELOSIM_REAL_FIFO=1`, using the rebuilt exporter. The native release and LLVM-instrumented suites each passed all six tests. The AddressSanitizer/UndefinedBehaviorSanitizer suite also passed all six tests. Optional Explicit real-HDL tests are outside the regular count. Builds used Ninja with four jobs. Logs are retained under `artifacts/verification-priorities/`.

The second full pass against instrumented libraries also passed all 317 tests. Combined Coverlet/LLVM measurements were 90.21% managed lines, 72.89% managed branches, 99.04% native lines and 87.12% native branches. LLVM reported 40 functions with mismatched profile data; that warning remains unresolved and qualifies the native coverage figures. The generated report is `artifacts/coverage/20261022-054543-9960868/index.html`. The default gate has zero configured minima; the final current-source check explicitly requires 85%/95% managed/native lines and 70%/85% branches.
