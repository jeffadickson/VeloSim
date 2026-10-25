# VeloSim handoff / continuation prompt

Read this first in a new chat or VS Code. Follow applicable AGENTS.md/user instructions. Use C#/F# automation and PowerShell/native file operations; keep meaningful code comments, stable StrictDoc IDs, line/branch coverage, negative/malformed tests, reproducible property/fuzz inputs and focused mutations. New helpers are C# file-based apps published as Linux NativeAOT executables. Existing StrictDoc Python configuration/runtime is a required-tool exception.

## Durable instruction

**Before EVERY commit, update this root prompt.md with current state, evidence, limitations and concrete continuation steps. Include it in that commit.** Do not insert the new commit's own hash into itself: obtain it afterwards with `git log -1`.

## Git and task state

- Repository: https://github.com/jeffadickson/VeloSim.git
- Initial requested push completed: origin/requirements-coverage-200 at de0cd6ce5a4ce38deeb2749ca073c83b1ab89d7a.
- Current branch: combinational-subroutines-200, based on de0cd6c. This handoff is updated immediately before the final local batch commit. Run `git status --short` and `git log -1 --oneline` to establish the actual saved state; do not assume an interrupted chat committed.
- User requested 200 numbered iterations, build every 10, AI peer review every 15 and final deep review before commit, focusing always_comb and functions/tasks. All 200 cases,20 scheduled builds and13 peer checkpoints passed. Three independent final source reviews accepted the bounded implementation. All three final AI evidence reviews accepted the measurements and bounded claims; see artifacts/comb-subroutines-200/reviews.md.
- The new batch is for a LOCAL commit. Do not push this branch unless requested. No PR, automation or active goal was created. No human expert or executed GitHub Actions claim.

## Open in VS Code / another chat

Windows PowerShell:

    Set-Location C:\Users\jeffd\Documents\Codex\2026-10-20\rea\work\VeloSim
    git status --short
    git log -1 --oneline
    code .

For native development use VS Code's WSL connection to Ubuntu-24.04 and open:

    /mnt/c/Users/jeffd/Documents/Codex/2026-10-20/rea/work/VeloSim

Tell the next chat: "Read root prompt.md, docs/combinational-subroutines-200.md and requirements/velosim.sdoc; inspect Git state and retained evidence before changing code. Continue one bounded open requirement with differential tests, coverage and review. Update prompt.md before every commit."

Linux-only PATH used for qualification:

    /home/jeffd/.local/bin:/opt/velosim/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

Use Ninja with 4 build jobs. Do not overlap primary builds/publications. Helpers and fixture paths currently target Linux-x64 on this host; /home/jeffd paths need adaptation on another machine. Linux-native fixture directories avoid mounted-filesystem build stalls. SDK 10.0.402, GCC 13, Clang/LLVM18, Ninja and Linux pwsh 7.6.7 were used.

## Current implementation

- RtlCombinational.cs: unified continuous/procedural acyclic graph, whole-word/concat writes, definite assignment, procedural blocking order, unique ownership, no own read-before-write, no clock/event-input dependencies, aggregate20000 expanded-statement budget. Settle before sequential sampling and after NBA commit. Public Continuous inventory retains original continuous assignments only.
- RtlConversion.cs: explicit argument/local/return width 1..128, signedness and two-state conversion barriers. Caller context cannot remove local/formal truncation.
- RtlDesign.cs integrates optional combinational IR, conversions, both emitters and blocking isolation against every settled RHS/condition read.
- native/uhdm/rtl_subroutines.h structurally interprets pure automatic function/task bodies into typed expressions using fresh frames. Actuals lower in caller context; nested function frames restore correctly. Procedural if handles X/Z as else. UHDM cloned locals bind by declaration type/scope/file/line/column. Task results are sampled together before copy-out.
- rtl.cpp checks each expression subtree and cumulative concat expansion. Global/memory/parameter captures are rejected. rtl_flatten.h collects always_comb with supported sequential processes.
- Cases001..120 include independent SDK first-eval, input-only/no-edge, rising-edge and repeated-eval assertions, plus standalone/SDK/Icarus full traces using the SAME original HDL. Workers1/4/8 check determinism; RTL evaluation is serial.
- Cases121..140 assert intended semantic diagnostics with reversed order/limb widths in the second half.141..160 are schema/declaration negatives.161..180 use128-bit actuals/results and1..128-bit signed formals/local overflow/two-state boundaries.181..200 reject actual unsupported HDL with specific diagnostics. Additional tests cover comb repeat, local parameter captures and cross-scope call rejection.

## Completed verification / retained evidence

Batch root: artifacts/comb-subroutines-200/. Generated artifacts are ignored by Git and are local evidence; keep them separately if moving machines.

- qualification-summary.json:40 groups of 5 passed cases;20 scheduled solution/native/exporter builds, Ninja 4; reviews.md:13 accepted checkpoints015..195 and final reviews. Early zero-test filters/setup failures are retained but excluded from qualification.
- Full run20261025-042139-8795316:831 executed/passed,0 failed; TRX 835 total with 4 Explicit opt-in tests excluded. Six native CTest checks passed. Full log coverage-final.log, TRX artifacts/tests/<RunID>/.
- Main report artifacts/coverage/<RunID>/index.html, Cobertura.xml, gate.json and source/binary evidence. Managed2930/3234lines90.60%,4363/5913branches73.79%; native runtime528/533lines99.06%,1087/1242branches87.52%. Explicit85/70/95/85line/branch gates passed. LLVM reported 43mismatched functions; retain that coverage limitation.
- Separate exporter-coverage/:344 fresh profiles, exact LLVM18/binary and six-source hashes;250/264lines94.70%,755/1052branches71.77%. No merge/export warnings. Separate denominator; no runtime threshold claim.
- helper-coverage/evidence.json: managed counterpart controller 23/23 lines (100%),28/40 branches (70%); mutation 73/73 lines (100%),47/66 branches (71.21%). Same-source managed IL measured; AOT machine-code coverage unmeasured. Native ELF binaries were verified and executed.
- Final NativeAOT mutation run mutations/20261025-040937-8751431/report.json and managed measured run20261025-041608-5991804:5/5 control,5/5 focused semantic faults caught. Faults: omit settling, disable definite assignment, disable read-before-write, corrupt second concat output, omit conversion. Infrastructure failures/compile failures/timeouts/zero tests never count as kills. Not a whole-program mutation score.
- Both guard properties ran 500 trials with seeds(20261025,864013)/(20261026,864013), shrinking disabled-guard counterexamples to(0uy,true), one shrink. Malformed structural IR fuzzing ran 500 trials seed 20261025; not a general HDL grammar fuzzer.
- sanitizers/report.json: generated models001/061/101/176 matched full36/36/36/24vector traces under ASan+UBSan, no diagnostics. Four models are bounded evidence, not all-generated-code coverage.
- Native bundle artifacts/bundles/20261025-040938-3068248/velosim-linux-x64.tar.gz passed packaged checks. Built from qualified dirty working tree at base de0cd6c before final commit, not from the eventual commit hash.
- frontend-evidence/index.json now retains485completed historical/final fixture reports and12,302referenced files, all SHA-verified. These are retained runs, not485distinct language requirements. It maps Linux fixtures to retained files; mutations/native-evidence-index.json retains 654 mutation/control fixture files. Original reports use Linux absolute paths; mirror indexes provide retained mappings.
- Final StrictDoc export: artifacts/comb-subroutines-200/strictdoc/html/. Stable narrower contracts VS-RTL-COMB-002, VS-RTL-FUNC-001 and VS-RTL-TASK-001 are Active. Broad COMB-001/PROC-001 remain Draft backlog. Relations are file-level with named test evidence; no automatic C# method tracing claim.
- coverage-current-precommit.log verifies current source/binary/report binding without rebuilding. After commit run the same readonly check and save coverage-current-postcommit.log. Do not rebuild measured binaries merely to recheck evidence.

## Reproduction commands (inside WSL checkout)

    dotnet build tests/VeloSim.Tests -c Release -m:4
    make combinational-qualification-tool JOBS=4
    artifacts/publish/CombinationalQualification/CombinationalQualification "$PWD" 1 15
    make combinational-mutation-probe JOBS=4
    make bundle JOBS=4
    make coverage JOBS=4

Set VELOSIM_REAL_FIFO=1 and VELOSIM_RTL_EVIDENCE_ROOT=/home/jeffd/.local/share/VeloSim/comb200/frontend for actual frontend coverage. Default exporter: artifacts/with-uhdm/bin/velosim-uhdm-rtl. Coverage exporter: /home/jeffd/.local/share/VeloSim/requirements-200/exporter-coverage/bin/velosim-uhdm-rtl (current rebuilt binary; prior retained copy is separate). Set VELOSIM_UHDM_RTL and a NEW dedicated VELOSIM_UHDM_PROFILE_FILE pattern when measuring exporter coverage. Never mix setup/old profiles. Runtime LLVM profiles are separate.

Readonly current-evidence check, with explicit thresholds:

    env VELOSIM_MIN_MANAGED_LINE=85 VELOSIM_MIN_MANAGED_BRANCH=70 VELOSIM_MIN_NATIVE_LINE=95 VELOSIM_MIN_NATIVE_BRANCH=85 artifacts/publish/VeloSim.Build/VeloSim.Build coverage-current artifacts/coverage/20261025-042139-8795316/Cobertura.xml

StrictDoc export uses the required tool runtime:

    uv tool run --from strictdoc==0.30.4 strictdoc export requirements --config strictdoc_config.py --output-dir artifacts/comb-subroutines-200/strictdoc --no-parallelization

NativeAOT restore changes compiler-only lock entries; those incidental changes were restored. SourceEvidence normalizes those compiler entries while retaining actual dependencies. The checkpoint helper freezes .cs/.cpp/.h/.csproj files only; final SourceEvidence covers wider build configuration. Neither covers the whole external toolchain.

## Remaining requirements / useful next work

Read the broader Draft requirements before selecting one. Frontend currently requires a supported sequential process; pure comb-only HDL modules remain unsupported, and direct IR still requires a scalar clock. Selected/memory combinational writes, latches, feedback/read-before-write and event-input-dependent comb logic remain rejected. Functions/tasks are pure automatic packed logic/bit only, bounded1..128bits and block/if/whole-local assignments; function-name returns work, explicit return/control flow, persistent/captured state, parameter captures, ref/inout, nested tasks and hierarchical call AST type 5000 remain unsupported. Static local syntax overwritten before every read can be observationally pure; do not claim universal static-local syntax rejection. Task output parents must be distinct whole words with combined width<=128; input/output aliases are supported via snapshot copy-out.

Candidates: qualify comb-only frontend designs without inventing clock semantics; extend pure function control flow one construct at a time; add safe constant-parameter capture using declaration identity rather than names. General scheduling, structures/unions/enums and dynamic containers remain broader work. A manual generate-localparam probe rejected unresolved P and was outside qualified scope; it did not demonstrate silently wrong output.

Report remaining unmeasured areas: upstream dependencies, generated C++ instruction coverage, NativeAOT instructions and uninstrumented configs/workflows. Keep historical docs/requirements-200.md metrics separate from this batch. Before any new commit, complete relevant tests/coverage/review and UPDATE THIS FILE.

Precommit closeout: all final source/evidence reviews accepted; the qualification summary status was refreshed, final StrictDoc export passed, frontend mirror finished485reports/12,302verified files, and the precommit current-source/binary coverage check passed. This file was updated immediately before the batch commit. Verify actual commit/status with Git; postcommit coverage-current is a readonly closeout step.
