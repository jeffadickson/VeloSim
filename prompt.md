# VeloSim continuation prompt

Read this file, docs/functions-100.md and requirements/velosim.sdoc first in another chat or VS Code. Follow applicable AGENTS.md/user instructions. Use C#/F# automation and native PowerShell file operations, Ninja with four jobs, meaningful comments, stable StrictDoc IDs, line/branch coverage, seeded properties/fuzzing, negative tests and focused mutations. Active helpers are C# file-based apps with Linux NativeAOT counterparts. Existing StrictDoc Python configuration/runtime is a required-tool exception.

**Before EVERY commit update this root prompt.md with the current outcome, evidence, limits and next steps, and include it in that commit.** Git is authoritative for the subsequent commit/push state; a commit cannot contain its own hash.

## Outcome and Git state

The authorized 100-case batch implements three bounded contracts: clockless combinational-only models, explicit function returns with statically bounded loops, and safe parameter/localparam captures. All 100 ordered cases, ten scheduled builds, six AI checkpoint reviews (every fifteen cases), and three final deep source reviews passed. Main regression and coverage passed; final actual-evidence review found no source blocker. These were AI reviews, not human expert reviews or GitHub Actions runs.

Branch: clockless-functions-100. Parent milestone: c4bd7ae. Origin: https://github.com/jeffadickson/VeloSim.git. This handoff is updated immediately before the final batch commit. The user explicitly authorizes pushing the completed branch, including its parent local milestone. Do not force-push, merge the default branch or create a PR without a request. No goal or automation was created.

Before continuing, inspect git status, the latest commit and remote branch SHA. Check artifacts/functions-100/final-evidence.json for the later actual commit/push verification. If that push is already complete, do not repeat development just because this file records precommit state.

## Open in VS Code or another chat

Windows PowerShell:

    Set-Location C:\Users\jeffd\Documents\Codex\2026-10-20\rea\work\VeloSim
    git status --short
    git log -1 --oneline
    git ls-remote origin refs/heads/clockless-functions-100
    code .

For native development connect VS Code to WSL Ubuntu-24.04 and open:

    /mnt/c/Users/jeffd/Documents/Codex/2026-10-20/rea/work/VeloSim

Linux PATH:

    /home/jeffd/.local/bin:/opt/velosim/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

Toolchain: SDK10.0.402, GCC13, Clang/LLVM18, Ninja, Linux pwsh7.6.7, StrictDoc0.30.4. NativeAOT currently targets Linux-x64. Helpers/evidence use /home/jeffd paths; adapt them on another machine. Do not overlap primary builds/publications or kill unrelated processes. Copied mutation experiments use separate workspaces.

Tell the next chat: "Read prompt.md, docs/functions-100.md and requirements/velosim.sdoc; verify Git and local evidence state. The bounded 100-case work is qualified. If its commit/push is unfinished, finish those authorized steps; otherwise select a documented open requirement, add reproductions and maintain traceability and coverage before changing code. Update prompt.md before every commit."

## Implementation invariants

- RtlDesign.Clock is nullable. clock:null requires a canonical empty sequential body. Emitters settle every actual input without inventing a clock or discarding an input named clk. Native Model accepts -1 only as the absent-clock sentinel; all other interface validation remains. SDK manifests/readmes describe both modes.
- Pure calls use continuation interpretation and fresh frames. Explicit return terminates its path, including enclosing loop increments. Evaluate nested return expressions before taking references into the caller return map, since frame restoration invalidates them. Branch-only loop locals must not escape path merges.
- repeat samples a known nonnegative count once, at most256. for uses a known local counter up to32bits, typed width/sign/context rules, one initializer/increment, counter+immutable-constant controls, at most256 trips, progress/wrap rejection and shared budgets. Nested-loop scanning must visit both singular/vector init/inc and++/--, deduplicate identities and retain its20k traversal bound. Body controls may not mutate an enclosing counter. Independent nested counters are qualified.
- Formal IODecl reads are allowed only inside pure calls and resolve through registered local identity/strict clone metadata; IODecl.Expr may link to its registered word. Parity regressions cross bits63/64 and read127. Selected local writes remain rejected.
- Parameter binding uses original typed declarations and visible elaborated instance context. Original lexical lookup honors variable/formal shadowing and rejects ambiguity. Exact file/coordinates reconnect malformed elaborated links; never substitute a caller parameter by spelling. Preserve original declaration types independently from override values. Known literal/unary-literal int32 and packed logic/bit values through128bits, including literal overrides, are qualified. Compound parameter expressions and parameter selections are rejected, including folded source forms, rather than trusting lossy UHDM folding. Whole parameters may pass through typed formals for later formal indexing. Hierarchical function calls/general constant evaluation remain unsupported.
- Existing acyclic fully assigned whole-word ownership/isolation rules still apply. General scheduling, latches/feedback, runtime-dependent loops, while/do, break/continue, persistent/mutable captures, ref/inout and broader aggregates/containers remain open. Consult Draft requirements before claiming completeness.

## Final verification

Local ignored batch root: artifacts/functions-100. Preserve artifacts separately when moving machines; Git contains code, fixtures and the summarized report, not all local binaries/traces.

- qualification-summary.json:100 passed numbered cases in20groups, ten builds and six accepted checkpoint reviews. Cases081–090 repeat five malformed IR shapes; they are not ten distinct features. reviews.md records fixes and AI acceptances. Later final regression requalifies earlier cases against the final nested-counter guard.
- Main run20261025-063443-6553218:959 executed tests passed, zero failures; TRX has959Passed and fourNotExecuted Explicit records (counter total963, notExecuted0). Both Release/instrumented CTest suites6/6. Same original HDL goes to Surelog/Icarus; workers1/4/8 check deterministic serial evaluation, not speedup.
- Main coverage gate: managed2935/3239lines90.61%,4381/5931branches73.87% (minimum85/70); native runtime528/533lines99.06%,1095/1246branches87.88% (minimum95/85). LLVM reported42functions with mismatched data during runtime export/render; native percentages remain qualified by that warning. Gate success does not resolve it. Reports/evidence/source manifest: artifacts/coverage/20261025-063443-6553218. Readonly coverage-current passed after restoring compiler-only generic lockfile entries; normalized application dependencies were unchanged.
- Separate six-file UHDM exporter coverage:460 fresh profiles,408/431lines94.66%,1157/1630branches70.98%; no threshold imposed. Exact retained binary and six current source hashes verified. Merge/export/LCOV/HTML stderr logs empty. JSON/LCOV/HTML/profiles/bindings: exporter-coverage/. Twenty-three lines and473branches remain uncovered.
- Native Core mutation run core-mutations/20261025-060609-0387127 and measured managed20261025-061422-3047273 each6passingcontrols+6semanticfaults detected. Final-source exporter runs mutations/20261025-062559-8591642 and20261025-062859-6024218 each3controls+3semanticfaults (return fallthrough, repeat+1, erased capture). Compile errors/timeouts/zero tests are never kills. Reports bind source and loaded binaries; copied workspaces protect the root.
- helper-coverage/evidence.json: controller26/26lines,40/60branches66.67%; Core mutator74/74,47/66branches71.21%; exporter mutator53/53,37/52branches71.15%. These measure managed counterparts of the same C# source; AOT instructions are unmeasured. Native ELF execution/signatures verified. HTML: helper-coverage/html/.
- ASan/UBSan sanitizers/report.json: six generated models and matching exporter outputs passed, vectors24/24/24/24/24/16; native external sanitizer suite passed. Generated-model leak checks enabled; exporter leak checks disabled; upstream UHDM uninstrumented. This is selected evidence, not all generated instruction coverage.
- Malformed clockless fuzz500trials seed20261026; FsCheck hidden-body property500trials seed(20261027,864013), with reproducible counterexample support. Supplemental actual tests cover signed/context sizing, scoped/nested loops, parameter scopes/overrides/types, two sourcefiles, parity reads and negative selection/compound/control guards.
- intro-example/report.json: published NativeAOT CLI generated the combined clockless/early-return/bounded-loop/BIAS-capture SDK; native host and original-HDL Icarus outputs match independently specified3,4,5,11,4. Original source/logs/host/traces retained.
- Native bundle artifacts/bundles/20261025-063254-5370740/velosim-linux-x64.tar.gz passed smokes. Built from qualified dirty working tree at basec4bd7ae before this commit, not from the eventual commit archive.
- StrictDoc IDs VS-RTL-CLOCKLESS-001 / VS-RTL-FLOW-001 / VS-RTL-PARAM-001 are Active with final-run evidence. Broader requirements remain Draft. Final export: artifacts/functions-100/strictdoc/html, strictdoc-final.log. File-level links/named tests maintained; automatic C# method extraction is not claimed.

Coverage excludes NativeAOT instructions, all-generated-model instruction coverage, upstream dependencies and uninstrumented configs/workflows; no MC/DC claim. Differential behavior, coverage, focused mutations and sanitizers are separate evidence, not proof of full Verilog compliance.

## Commands and remaining backlog

Inside WSL checkout:

    make test JOBS=4
    make coverage JOBS=4
    make function-mutation-probe JOBS=4
    make combinational-mutation-probe JOBS=4
    make bundle JOBS=4
    artifacts/publish/CombinationalQualification/CombinationalQualification "$PWD" 1 15 functions100

Use a NEW dedicated VELOSIM_UHDM_PROFILE_FILE for exporter measurement; never mix old/setup profiles. The retained merge script is artifacts/functions-100/merge-exporter.ps1; refresh-frontend.ps1 preserves finalized fixture references and verifies hashes. These are local file/report operations, not shipped simulator helpers.

Readonly current-source/binary gate (do not substitute an older run):

    env VELOSIM_MIN_MANAGED_LINE=85 VELOSIM_MIN_MANAGED_BRANCH=70 VELOSIM_MIN_NATIVE_LINE=95 VELOSIM_MIN_NATIVE_BRANCH=85 artifacts/publish/VeloSim.Build/VeloSim.Build coverage-current artifacts/coverage/20261025-063443-6553218/Cobertura.xml

Requirements export:

    uv tool run --from strictdoc==0.30.4 strictdoc export requirements --config strictdoc_config.py --output-dir artifacts/functions-100/strictdoc --no-parallelization

Next development candidates: broader event/delay scheduling and interacting processes; runtime procedural loops and richer functions/tasks; general constant expressions/parameter selections; broader hierarchy/generate scopes, types and arrays. Preserve fail-closed diagnostics until each new contract has independent same-input comparison and negative/resource tests. Investigate the runtime LLVM42-function profile mismatch before treating its percentages as unqualified coverage.

Final precommit closeout: all reviews, StrictDoc and gates completed. The final payload mirror verified571 accepted reports and14526 referenced files; final source/retained-report membership hashes matched. final-evidence.json records the completed proof. This prompt was updated immediately before the batch commit. Next: commit the staged intended code/docs/lock; rerun readonly coverage-current; push -u origin clockless-functions-100; verify remote SHA equals HEAD and clean status; record actual SHA/push in ignored final-evidence.json. If Git/evidence show those steps already completed, leave completed history intact.
