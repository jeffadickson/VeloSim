# Procedural, type and array evolution

This branch extends the bounded RTL backend. The fresh full regression passed 1,024 executed tests; retention, tool fingerprints and current-source/binary checks passed and are recorded in local artifacts. Full SystemVerilog procedural and container semantics remain open.

The new execution path allocates storage, executes constant declaration initializers, then executes one ordered, untimed initial block. Runtime while/do/repeat/for loops share a 20,000-step evaluation budget. Repeat snapshots its count. Loop controls use collected module storage; statement-local loop declarations, break/continue and suspension remain outside the supported subset. Failed SDK evaluations restore model storage and edge history; native VPI callback side effects and queued drives are not transactional.

Integral storage now includes byte, shortint, int, longint, integer, time and supported integral enums. Packed structures follow declaration order; equal-width untagged unions overlay storage. Uniform member state is required. The RTL runtime uses four 64-bit limbs for words through 256 bits. External SDK consumers must rebuild against word ABI 2 and matching headers/runtime; the independent IR/scheduler word type remains 64 bits.

Fixed arrays support ranks 1–4 and a total of at most 256 elements. Every coordinate is checked before linearization. Unknown/out-of-bounds reads return X and writes are ignored. Address expressions retain the existing unsigned-index requirement; signed indices need explicit unsigned casts. Two-state storage starts at zero and coerces X/Z on writes. Invalid reads remain X, including from two-state elements.

## Frontend correction

The pinned Surelog revision `6db44ea2048c8ab9c702e389c2897f7e2318ccc5` preserves the tagged-union AST keyword but fails to write its UHDM flag. `tools/surelog-tagfix` recompiles two source translation units against the installed matching native SDK. SHA-256 checks reject different input sources. The correction preserves the flag and appends a root-level capability marker after elaboration, before serialization. VeloSim rejects union models without this marker, avoiding acceptance based on missing tag information.

Build a private frontend without replacing the installed compiler:

```powershell
git clone https://github.com/chipsalliance/Surelog.git /path/to/Surelog
git -C /path/to/Surelog checkout 6db44ea2048c8ab9c702e389c2897f7e2318ccc5
make surelog-tagfix SURELOG_SOURCE=/path/to/Surelog JOBS=4
$env:VELOSIM_SURELOG = '/absolute/path/to/artifacts/surelog-tagfix/surelog'
```

Use the matching pinned Surelog/UHDM SDK documented in `real_uhdm_gate.md`. The marker is a compiler capability record, not cryptographic authentication of arbitrary edited databases.

## Verification and remaining work

Tests feed unchanged HDL to Surelog and Icarus, then compare standalone and external SDK execution with independent expected traces where applicable. Wide arithmetic also uses a BigInteger/string oracle. Rank-three/four invalid inner coordinates exposed Icarus aliasing: retained tests-06 evidence records the discrepancy. Valid coordinates use differential comparison; invalid coordinates use independent SDK and native coordinate-guard tests rather than adopting the reference defect.

Focused mutation tests compile a passing copied-runtime control and four semantic mutants (carry, stride, coordinate guard, two-state coercion). FsCheck and malformed startup fuzzing retain reproducible seeds. The fresh full regression and coverage gate are recorded below; earlier investigation attempts are retained separately.

Dynamic arrays, associative arrays, queues, multiple time-zero processes, general event/delay suspension, runtime loops inside pure functions, unpacked aggregates and mixed-state packed aggregates remain open. StrictDoc contains six Active narrow contracts and implementation/test relations. Broad procedural, type and array requirements remain open.
Packed-type queries also share a 20,000-step traversal budget. A small synthetic UHDM graph with eight levels of eight shared union members verifies bounded rejection and budget recovery after exceptions. This prevents exponential type traversal without requiring a large HDL input.

## Qualification evidence

The accepted full test/coverage run is `20261025-151434-5000821`. Its TRX records 1,024 executed passes, zero failures and four conditionally gated legacy tests not executed (1,028 definitions). Both ordinary and instrumented native CTest runs passed six tests; the UHDM-enabled focused and ASan/UBSan suites passed seven, including the shared-type graph guard. The four optional legacy counter/word/Icarus tests remain unmeasured in this run.

| Coverage scope | Lines | Branches |
|---|---:|---:|
| Managed aggregate, Coverlet/ReportGenerator | 2,986 / 3,290 (90.76%) | 4,524 / 6,093 (74.25%) |
| Native runtime, LLVM reported values | 560 / 565 (99.12%) | 1,198 / 1,356 (88.35%) |
| Seven first-party UHDM exporter files, separate 516-profile merge | 474 / 498 (95.18%) | 1,376 / 1,922 (71.59%) |

Main thresholds were managed 85% line / 70% branch and native 95% / 85%; they passed. Native percentages require qualification: LLVM18 reported 59 rejected zero-hash mappings across 38 first-party runtime/Port functions, including new limb operations. All 38 also have actual nonzero profile records; the earlier attribution diagnostic (attempt2) division record retains 131,981 calls. This suggests duplicate unused mappings, but the complete effect on line/branch denominators has not been proven. The warnings are retained, not suppressed. The separate exporter merge had no diagnostic warnings and verified every measured source hash against its frozen binary binding.

The synthetic UHDM graph test has a separate packed-header-only report: 17 / 28 lines (60.71%) and 22 / 70 branches (31.43%). It exercises budget success, rejection and recovery; it does not call the packed-field functions. These figures are not combined into the production exporter totals. MC/DC is unmeasured.

Additional evidence:

- FsCheck malformed-shape properties: 500 cases, replay seed `(20261028, 864013)`.
- Malformed startup/loop fuzzing: 500 trials, seed `20261029`.
- Copied-runtime semantic mutations: a passing compiled control and all four faults detected (carry, stride, coordinate upper bound, two-state coercion). Compile failures do not count as detection. Scope is selected operators, not exhaustive mutation coverage.
- Independent BigInteger/string expectations at widths 129, 191, 192, 255 and 256, including signed division/remainder/shifts and decimal/hex constants, alongside unchanged-HDL Icarus/native/SDK comparisons.
- Seven generated standalone models passed ASan/UBSan traces and matching sanitized-exporter IR: wide256, union overlay, selected packed writes, rank4, distinct instance initialization, single-clock loops and rank1 bit memory. Model leak detection was enabled; exporter/UHDM graph leak detection was disabled and upstream UHDM was uninstrumented. This is selected generated-code verification, not exhaustive instruction coverage.
- NativeAOT CLI exported wide256 and rank4 SDKs; generated header/runtime sources matched managed exports. The local Linux-x64 native bundle is `artifacts/bundles/20261025-135847-6920889/velosim-linux-x64.tar.gz`.
- Three independent read-only AI source reviews accepted the bounded implementation. Actual-evidence review checks the final hashes and measured scopes; these are AI reviews, not human expert signoff.

Local evidence lives in `artifacts/procedural-types-arrays`: `coverage-final.log`, exporter/type-guard coverage reports, sanitizer logs, `sanitized-models/report.json`, `native-cli/report.json`, final fixture/tool fingerprint indices and final source-binding checks. Main HTML/Cobertura coverage is `artifacts/coverage/20261025-151434-5000821`. NativeAOT instructions, upstream libraries, build/configuration logic and all generated instructions are not fully measured by main line/branch coverage.

The final rerun used a private ccache4.9.1 compiler launcher with Ninja jobs4. It reused 1,271 of 2,241 compilation calls (56.72%); all test processes and comparisons still executed. The launcher fingerprint, settings and statistics are retained in `compiler-cache`.

Earlier logs are investigation evidence: attempt1 was interrupted to fix resource/metadata/harness issues; attempt2 passed 1,023 cases and failed one obsolete 129-bit rejection assertion. That assertion now tests 257-bit rejection and 256-bit acceptance, and the fresh complete rerun passed. Old rejection cases were moved to adjacent unsupported forms as initialization, packed types and fixed arrays gained support.
Final retention verified 460 passing fixture reports and 11,990 referenced files in "frontend-evidence-final". Tool fingerprints and the current-source/binary coverage gate passed after restoring only compiler-specific NuGet lock churn. Final evidence review accepted the bounded result with the LLVM limitation retained. The root "prompt.md" is updated immediately before the local commit; Git and local final-evidence.json establish the later push state.
