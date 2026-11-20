# Library implementation: qualification in progress

The active task implements the first three opportunities discussed with the user, plus C# JSON/CLI improvements: generated UHDM traversal, an SVInt prototype with an evidence-based adoption decision, shared standard-container adapters, and System.Text.Json/System.CommandLine integration. Existing associative-array work is preserved. No general timed scheduler, wider production word ABI, FST implementation or broad Verilator parity is claimed.

## Implemented slices

- `native/uhdm/rtl_audit.h` uses the already-pinned `UhdmListener`, including shared-node tracking and bounded traversal, before flattening. Unsupported assertions, final processes, clocking/program/interface constructs and primitive collections have explicit source-located rejection. Existing typed/scoped lowering remains responsible for its narrower semantic checks. This does not implement assertions; it prevents accepted constructs from disappearing. Regression checks include an inactive generate assertion that must not reject the executable design.
- `ElementType` and the common container capacity in `runtime.h` supply element conversion/default/capacity policy to both generated adapters and `containers.h` wrappers. Owning storage remains `std::vector`, `std::deque` and `std::map`. Queue push/pop are shared templates; primitive conversion no longer allocates a one-element dynamic array. Primitive indexed queue writes retain their existing bounds contract; generated HDL queue writes at size still append.
- `CliOptions.cs` uses pinned System.CommandLine 2.0.14 for repeated option parsing and generated help. The integration deliberately preserves duplicate rejection in FIFO/seeded workflows and last-value behavior in other legacy workflows, disables response-file expansion and option bundling, and keeps numeric syntax invariant. Workflow/domain validation remains in existing command handlers.
- Source-generated System.Text.Json DTOs serialize the existing CLI error and exported model manifest contracts. Schema names, manifest field meanings, word ABI 2 and generated input hashes remain unchanged. The change does not replace all internal JSON or add another serializer dependency.

## Numeric prototype and current adoption decision

The optional `VELOSIM_SVINT_QUALIFICATION` CMake target uses slang **12.1.0**, source tag `v12.1`, commit `42710c788ce76cd08ce2080773e0b8286c428a05`. Slang is built separately with tools/tests/docs/Python bindings and mimalloc disabled, then located through its CMake package. Upstream's required generators remain upstream dependencies; no custom Python helper is added. Normal VeloSim/runtime/SDK builds do not acquire slang merely by including this optional module.

`tools/GenerateNumericCorpus.cs` is a .NET 10 NativeAOT executable that builds an independent BigInteger/explicit-bit-truth-table TSV corpus. CMake builds it incrementally and reruns it only when its inputs change. `svint_qualification.cpp` checks the candidate and current runtime independently against those expected values. Candidate widths cover 1 through 1,024 at selected boundaries; production runtime checks stop at its existing 256-bit limit.

The accepted prototype run checks **26,624 oracle rows**, including **21,632 current-runtime rows**, seed `20261027`. Cases include signed/unsigned add/subtract/multiply/divide/remainder/bitwise operations, zero divisors, numeric boundaries and four-state fill truth tables. These are finite directed/randomized cases, not complete language conformance.

A separate Release-build known-value 256-bit multiply/add recurrence performs 50,000 iterations, one warmup and three measured repeats, excluding parsing/conversion and whole-model execution. Current-core times were 134,003 / 97,502 / 97,502 ns; SVInt times were 3,197,862 / 3,247,462 / 3,487,467 ns, with equal full-width final checksums. This narrow result supports **retaining the current production arithmetic** and keeping SVInt as an optional qualification/wider-value reference. It does not establish performance for other operations, X/Z workloads, native Windows or complete simulations. No automatic production backend substitution or ABI expansion is made.

Raw native evidence is under `~/.local/share/VeloSim/library-implementation`; the numeric build/corpus is in the separate `svint-qualification-build`. A current-source, versioned qualification report and reproduction instructions will be finalized after the remaining checks. Earlier compilation/adaptation failures remain diagnostic records rather than accepted passes.

## Verification still in progress

The first focused native run passed 4/4, followed by 11/11 native CTests. The new CLI scope passed 16/16, including a seeded 100-case argument-boundary property. A broader Windows attempt passed 13, failed 4 and skipped 1 because of missing native DLLs/symlink privilege; those are not Windows platform qualification. Linux regressions, additional actual-HDL checks, sanitizer/coverage, mutation and NativeAOT CLI packaging remain open. No final completion or requirement closure is claimed by this checkpoint.
