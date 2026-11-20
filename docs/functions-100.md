# Clockless models, function control flow and constant captures

This batch completes 100 numbered qualification cases, ten scheduled builds (every ten cases), six independent AI checkpoints (every fifteen), and a final deep source review by three AI peers. The prior milestone is c4bd7ae. These are bounded additions to the structural RTL backend; broader SystemVerilog support remains open.

| Cases | Contract |
| --- | --- |
| 001–020 | Clockless reverse-order combinational graphs, widths 1–128, an ordinary input named clk, SDK first eval/input changes/repeated eval/time advance |
| 021–040 | Explicit returns, conditional early exit, unreachable trailing statements, combinational and clocked callers |
| 041–060 | Known repeat/for bounds, zero iterations, increments, return inside loops, four-state runtime data/conditions |
| 061–075 | Module parameters and function localparam shadowing |
| 076–080 | Two elaborated instances with different overrides of the same parameter spelling |
| 081–090 | Ten malformed clockless-body cases over five repeated shapes |
| 091–100 | Invalid loop controls/counts, counter mutation, unknown parameter and unassigned-return rejection |

The positive fixtures pass the **same original HDL** to Surelog and Icarus. They compare Icarus traces with standalone models using workers 1/4/8 and with a generated SDK host, retaining source, tool, command and trace hashes. Worker counts verify deterministic serial execution; they do not measure parallel speedup. Seeded vectors use 20261026 + case ID and include X/Z. The actual case/build/review records are in `artifacts/functions-100/qualification-summary.json` and `reviews.md`.

## The three execution contracts

A null clock means a combinational-only model. The standalone driver samples every actual input and settles once per row; the SDK settles on eval, including the first eval of a constant-only design. There is no synthetic clock port, and an actual input named clk keeps its declared width and value. The native interface accepts clock index -1 as the absence sentinel, while retaining port and index validation. The JSON IR requires a canonical empty sequential body when clock is null. Existing acyclic, fully assigned, whole-word ownership rules apply.

Pure automatic functions now support explicit return expressions and bounded loops. A continuation interpreter evaluates the remaining statements separately along each conditional path. Return terminates that path, including enclosing loop increments, before final results merge. Calls preserve conversion boundaries, fresh frames and local scope; nested return expressions are evaluated before taking references into the caller's restored frame.

Repeat samples a known nonnegative count once. For uses a known local counter no wider than 32 bits, typed width/signedness/context rules, one initializer and increment, and a proven trip count no greater than 256. Controls may read only that counter and immutable constants. Body scanning includes nested loop initializers/increments and rejects changes to the enclosing counter. Progress, wrap, node, depth and expansion limits are enforced. Runtime-dependent loops, while/do, break/continue, timing and mutable captures remain unsupported.

Parameter captures bind an original typed declaration to the visible elaborated instance. Original lexical lookup honors variable/formal shadowing; exact file/line/column metadata reconnects malformed elaborated links only when unambiguous. The original declaration type survives corrupted elaborated shadow types. This resolved real Surelog/UHDM unbound references, shadow misbindings and premature folding encountered in qualification. VeloSim does not repair these by rewriting HDL or borrowing a same-named caller parameter.

Qualified captures are known literal or unary-literal int32 and packed logic/bit values through 128 bits, including literal instance overrides. Compound expressions and parameter selections are explicitly rejected, including folded source forms that could have lost bits upstream. A whole parameter may be passed through a typed formal and that formal indexed. Unknown/ambiguous captures, general constant-expression evaluation and hierarchical function calls remain open.

## Why the reused libraries helped

Surelog supplies preprocessing, parsing and elaboration; UHDM supplies typed objects, declaration identities, hierarchy and control flow. That avoids developing a SystemVerilog parser and elaborator before implementing execution. VeloSim still owns subset validation, pure-call expansion, the JSON IR, combinational ordering, four-state operations and generated model behavior. Broad frontend acceptance does not imply broad simulation support.

The C# compiler layer validates the exported IR and emits native C++. CMake/Ninja compiles models and the exporter with four jobs. The C++ runtime supplies four-state words and external interfaces. Icarus independently executes the original HDL for comparison; it is a verification oracle, not VeloSim's execution engine. See the [illustrated introduction](introduction.html) for the complete pipeline and the earlier VPI/model-host interface examples.

A combined public-CLI example exercises all three additions:

```systemverilog
module dut(input logic [7:0] a, output logic [7:0] q);
  parameter int BIAS = 3;
  function automatic logic [7:0] count_with_bias(input logic [7:0] x);
    logic [7:0] n;
    logic [31:0] i;
    begin
      if (x == 8'b0) return BIAS;
      n = 0;
      for (i = 0; i < 8; i++) n = n + x[i];
      return n + BIAS;
    end
  endfunction
  always_comb q = count_with_bias(a);
endmodule
```

The published NativeAOT `VeloSim.Tools rtl-export` produced an SDK from this design's UHDM-derived JSON. Its C++ host and Icarus both returned 3, 4, 5, 11, 4 for inputs 0, 1, 3, 255, 128, matching independently specified expected values. Exact original source, tool logs, host and traces are retained under `artifacts/functions-100/intro-example/`. Generation still requires the frontend/build tools; the compiled host executes natively.

## Verification evidence

Additional differential regressions cover signed negative counters, assignment-context sizing and wrap rejection, scoped int counters, mixed signed/unsigned arithmetic in both orders, explicit return of an assigned function result, signed 128-bit parameters, observable module/local shadowing, nested sibling overrides, two original source files, differing shadowed types, formal bit reads across 63/64 and at 127, and independent nested loops. Two exact-diagnostic negative tests reject nested loop controls that mutate an enclosing counter. The checkpoint-060 review found that last proof gap; the final source closes it and retains the reproductions.

Seeded malformed-IR fuzzing and FsCheck properties each run 500 trials. Reproducible seeds are 20261026 and (20261027, 864013), with counterexamples retained by the test tooling. These supplement actual malformed HDL and differential tests.

Focused native and measured managed Core mutation experiments each passed six controls and detected six intended faults, including the clockless empty-body guard. Final runs: `core-mutations/20261025-060609-0387127` and `20261025-061422-3047273`. Exporter mutation experiments each passed three controls and detected return fallthrough, repeat off-by-one and erased captures: `mutations/20261025-062559-8591642` and `20261025-062859-6024218`. Successful compiler/native commands and marked semantic trace mismatches are required; compilation failures, timeouts and zero-test runs do not count as kills. Copied source/build workspaces protect the measured root, and reports bind source and loaded binary hashes. This is focused mutation testing, not a whole-program mutation score.

All three C# qualification helpers have NativeAOT executables and measured managed counterparts:

| Helper | Covered lines | Covered branches |
| --- | --- | --- |
| Ordered qualification | 26/26 (100%) | 40/60 (66.67%) |
| Core mutation probe | 74/74 (100%) | 47/66 (71.21%) |
| Exporter mutation probe | 53/53 (100%) | 37/52 (71.15%) |

`helper-coverage/evidence.json` binds these results to source, managed and native ELF hashes. The managed counterpart measures the same source's IL; NativeAOT instructions are unmeasured. Twenty, nineteen and fifteen branch paths respectively remain unhit. HTML is retained under `helper-coverage/html/`.

Selected ASan/UBSan checks passed for six generated models and matching sanitizer-exporter outputs: clockless001, return024, loop052, nested instance shadowing, bit63/64 parity and independent nested loops. The first five each compared 24 vectors; the last compared 16. Native external-interface sanitizer tests also passed. Generated-model leak checks were enabled; exporter leak checks were disabled and upstream UHDM was uninstrumented. Exact IR/trace/binary/source bindings are in `sanitizers/report.json`. These checks do not measure all generated instructions.

The Linux-x64 NativeAOT bundle `artifacts/bundles/20261025-063254-5370740/velosim-linux-x64.tar.gz` passed packaged smoke checks. It was built from the qualified working tree at base c4bd7ae before the final commit. The helpers currently use Linux-specific evidence paths; another machine must adapt them.

The final full regression `20261025-063443-6553218` passed **959 executed tests with zero failures** in 34 minutes 12 seconds. The TRX contains 963 results: 959 Passed and four NotExecuted Explicit tests. The summary's notExecuted counter is zero despite those four result records; this report uses the actual result records. Both Release and instrumented native CTest suites passed 6/6. This full run requalifies the earlier numbered cases against the final nested-counter guard and all later fixes.

| Measurement scope | Covered lines | Covered branches | Gate minimum line/branch |
| --- | --- | --- | --- |
| Managed main suite | 2935/3239 (90.61%) | 4381/5931 (73.87%) | 85% / 70% |
| Native runtime/JSON | 528/533 (99.06%) | 1095/1246 (87.88%) | 95% / 85% |
| Six first-party UHDM exporter files, separate measurement | 408/431 (94.66%) | 1157/1630 (70.98%) | Reported separately; no threshold imposed |

The main `gate.json` passed. Its `evidence.json` and `sources.json` bind measured report, source, tool and binary fingerprints. A readonly `coverage-current` check also passed after restoring compiler-only NativeAOT lockfile entries; normalized application dependency snapshots remained identical. The main HTML report is `artifacts/coverage/20261025-063443-6553218/index.html`.

**Runtime coverage limitation:** LLVM emitted “42 functions have mismatched data” during runtime export/render. The runtime percentages above are the tool's reported values and remain qualified by that warning; a passing numerical gate does not resolve it. The 959 behavioral test passes are separate evidence. The six-file exporter measurement merged only the 460 fresh profiles from this run with the exact retained Clang18 instrumented binary. Source hashes matched, all six expected filenames were checked, and merge/export/LCOV/HTML stderr logs were empty. Its JSON/LCOV/HTML, profiles and source/binary bindings are under `exporter-coverage/`.

Main line/branch coverage does not measure NativeAOT instructions, all generated-model instructions, upstream libraries, excluded workflows/configuration or MC/DC. Exporter totals include 23 uncovered lines and 473 uncovered branches. Coverage alone is not proof of language correctness. Differential traces, negative tests, properties/fuzzing, focused mutations and selected sanitizers provide separate checks.

Final source reviews accepted the bounded implementation. Actual-evidence reviews and final StrictDoc export are recorded in the local ledger; Git and the final evidence manifest establish the subsequent commit/push state.

Stable StrictDoc IDs are VS-RTL-CLOCKLESS-001, VS-RTL-FLOW-001 and VS-RTL-PARAM-001. These three bounded contracts are Active with final-run evidence; broader scheduling, hierarchy, types, procedural and array requirements retain their existing incomplete status. File-level relations and named tests provide traceability; automatic C# method extraction is not claimed.

Local reports under `artifacts/` are ignored by Git. Preserve that directory separately when moving machines. `prompt.md` explains how to resume in another chat or VS Code and must be updated before every commit.
