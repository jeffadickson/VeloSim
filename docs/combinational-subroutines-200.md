# Combinational processes and automatic subroutines: qualification results

The prior completed work was pushed to origin/requirements-coverage-200 at de0cd6c before this batch. New work is on combinational-subroutines-200. This ledger distinguishes numbered qualification cases from language features; no human review is claimed.

The batch completed 200 numbered qualification cases, twenty scheduled builds and thirteen independent AI peer checkpoints. Final source reviews accepted the bounded implementation; the actual regression and coverage results appear below.

| Cases | Intended contract |
| --- | --- |
| 001–020 | Reverse-source-order combinational DAG, pre-edge sampling and post-NBA settling |
| 021–040 | Blocking order, full if/case assignment, X/Z procedural conditions |
| 041–060 | Automatic pure functions, fresh locals and procedural branches |
| 061–080 | Nested function calls and signed narrow formal/return boundaries |
| 081–100 | Zero-time task inputs/outputs and local branch computation in always_comb |
| 101–120 | Tasks in both combinational and sequential processes |
| 121–140 | Semantic safety rejection matrix |
| 141–160 | Malformed schema/declaration boundaries |
| 161–180 | Actual 128-bit input/output calls across 1–128-bit signed formals, local overflow and two-state conversion |
| 181–200 | Actual HDL rejection boundaries including timing, aliases, captures, partial assignment, recursion, selected local writes and expansion |

Each positive frontend fixture feeds the exact same original HDL to Surelog and Icarus, then compares full standalone and SDK traces. Cases001–120 additionally include independent SDK assertions at initial evaluation, input-only updates while clock stays low, rising edge and repeated eval. Seeded stimuli use 20261025 + case ID. Workers1/4/8 check deterministic results, not RTL parallel speed.

Bounded scope: combinational writes currently require whole words, complete assignments, acyclic dependencies, no own storage read before definite assignment, and no dependency on event inputs. The design still requires at least one supported sequential process. Functions/tasks use automatic declarations and only structurally pure computation; static storage cannot supply retained values. Explicit return, global mutable captures, delay/event control, ref/inout, selected task output actuals and nested tasks remain unsupported. Function-name return assignment is supported. General always_comb/functions/tasks requirements remain open beyond these bounded contracts.

Early source reviews caught and corrected continuous self-feedback and missing procedural-reader isolation checks. Selected local writes now require a whole declaration, and symbolic expansion is bounded during conversion and branching. Initial test setup errors (missing whitespace and a zero-test filter) do not qualify any iteration. UHDM formal/return binding probes are retained separately from accepted cases.

Example of the targeted subset:

```systemverilog
module dut(input logic clk, input logic [7:0] a,
           output logic [7:0] q, next_value);
  function automatic logic [7:0] increment(input logic [7:0] x);
    increment = x + 8'b1;
  endfunction
  always_comb next_value = increment(a);
  always @(posedge clk) q <= next_value;
endmodule
```

The function formal and return are sizing boundaries. VeloSim structurally evaluates the pure function body into typed expressions, retaining truncation/extension at every local assignment. That representation makes its input dependencies visible to the same acyclic settle graph used by continuous assignments. It does not execute the function by string-rewriting HDL. Tasks similarly produce bounded zero-time local computation and explicit output copy-out, preserving caller blocking ownership rules. Unsupported state, timing and aliases are rejected rather than approximated.

## Completed checkpoint and focused gates

The ordered200 numbered cases passed, all twenty scheduled Release solution/native/exporter builds passed, and thirteen AI peer checkpoints were accepted. Three final deep SOURCE reviews accepted the bounded implementation; all three final AI evidence reviews accepted the reported results and bounded claims. The verified ledger is artifacts/comb-subroutines-200/qualification-summary.json and the detailed review record is reviews.md beside it.

Native bundle publication passed at artifacts/bundles/20261025-040938-3068248/velosim-linux-x64.tar.gz, including packaged smoke checks. It was built from the qualified working tree at base de0cd6c with dirty status; it is not an archive built after the eventual final commit.

Final-source NativeAOT mutation run: artifacts/comb-subroutines-200/mutations/20261025-040937-8751431/report.json. Its five-test control passed, then all five focused faults produced the intended semantic failures: omitted settling, disabled definite-assignment guard, disabled read-before-write guard, corrupt second concat copy-out, and omitted conversion barriers. Native failures require successful compiler/executable command evidence plus the marked trace mismatch; compiler failures, timeouts, zero tests and unexpected exceptions never count. The managed counterpart also passed its control and caught all five faults under coverage. This is a focused experiment, not a whole-program mutation score.

Both guard properties ran500 trials with replay seeds(20261025,864013) and(20261026,864013). Deliberately disabled guards minimized to(0uy,true), one shrink, with original inputs and replay details retained in TRX. Structural malformed-IR fuzzing ran500 trials with seed20261025. This is not a general SystemVerilog grammar fuzzer.

Generated models001,061,101,176 passed ASan+UBSan with full matching36,36,36,24-vector traces and no diagnostics. Their report records source-report, generated-C++, executed-binary and retained-copy hashes at artifacts/comb-subroutines-200/sanitizers/report.json. Four selected models are bounded sanitizer evidence, not coverage of all generated code.

## Helper source coverage already measured

| Component | Lines | Branches |
| --- | --- | --- |
| Checkpoint controller managed counterpart |23/23(100%)|28/40(70.00%)|
| Mutation helper managed counterpart |73/73(100%)|47/66(71.21%)|

Evidence is helper-coverage/evidence.json and each component's coverage.cobertura.xml/run.log beneath artifacts/comb-subroutines-200/. Source/native/managed/report hashes are retained. Both native helpers have verified ELF signatures and executed successfully; the coverage measurement is managed IL of the same source. NativeAOT instructions are unmeasured. Twelve controller branches and nineteen mutation-helper branches remain unhit despite full line coverage.

## Execution and reproduction limits

The native qualification helpers currently target Linux-x64 and use this host's /home/jeffd evidence roots. The controller freezes source filenames and hashes for .cs/.cpp/.h/.csproj files; final SourceEvidence covers the wider build configuration. Do not treat the checkpoint snapshot as a complete compiler/configuration provenance system by itself. Fixtures additionally fingerprint their original HDL, compile inputs, tools and compared artifacts.

The full main regression passed831 executed tests with zero failures; four Explicit opt-in tests were excluded (TRX835 total). Six native CTest checks passed. The final coverage gate passed; the following measurements are retained separately by scope. General timed scheduling, always_comb outside the stated DAG contract, arbitrary return/control flow, persistent/captured subroutine state, ref/inout, general aggregates and dynamic containers remain open.

## Final regression and coverage

The main run is `20261025-042139-8795316`: full log at `artifacts/comb-subroutines-200/coverage-final.log`, TRX beneath `artifacts/tests/20261025-042139-8795316/`, and combined report, gate and source/binary manifests beneath `artifacts/coverage/20261025-042139-8795316/`. The test duration was32m40s. The instrumented exporter processed344fresh profiles from that suite.

| Scope | Lines | Branches |
| --- | --- | --- |
| Managed simulator/tool assemblies |2930/3234(90.60%)|4363/5913(73.79%)|
| Native runtime/interface sources |528/533(99.06%)|1087/1242(87.52%)|
| Separate first-party UHDM RTL exporter, six sources |250/264(94.70%)|755/1052(71.77%)|

Managed minimums85%line/70%branch and native runtime minimums95%line/85%branch passed. Exporter coverage is a separate measurement without those runtime thresholds. Its LLVM18 JSON, LCOV, HTML, exact binary/source bindings and344-profile manifest are retained under `artifacts/comb-subroutines-200/exporter-coverage/`; no exporter merge/export warning was emitted. The runtime coverage process reported43functions with mismatched LLVM data. Those warnings are retained in the main log and limit confidence in the affected coverage data; they do not turn coverage into behavioral proof.

Coverage excludes upstream Surelog/UHDM/Icarus, generated-model instruction coverage, NativeAOT machine code and uninstrumented configuration/workflow code. Behavioral differential traces, targeted sanitizers and mutation results are independent evidence, not additions to these coverage denominators. The four excluded Explicit tests did not run in this suite; current indexed tests do exercise the actual frontend/reference tools with `VELOSIM_REAL_FIFO=1`.

The narrower StrictDoc contracts VS-RTL-COMB-002, VS-RTL-FUNC-001 and VS-RTL-TASK-001 are qualified by these cases and guards. Their status does not close the broader COMB-001/PROC-001 backlog. File relations and named test evidence are maintained; automatic C# method-level trace extraction is not claimed.

Final review: simulation, C# and tooling AI peers independently accepted the final source and evidence. Their one archival correction—the qualification summary still saying coverage was pending—was fixed. The final StrictDoc export passed22source pages. Generated evidence remains local and ignored by Git; preserve it separately when moving the checkout.

The final frontend mirror completed485passed historical/final fixture reports and12,302referenced files with matching SHA hashes. These retained runs are not485distinct language features or requirements. The index maps original absolute Linux references to the retained copies; the separate mutation index retains654files. Final main source/binary binding passed again before commit.
