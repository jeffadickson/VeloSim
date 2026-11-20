# SVInt: 128-bit SIMD opportunities

Implementation follow-up: an opt-in public-SVIntStorage adapter now executes target-isolated AVX128 and scalar paths. Directed/property checks passed 27,648 backend comparisons plus 6,336 comparisons with VeloSim's existing runtime. A seeded 10,000-case libFuzzer differential run passed. Complete-adapter XOR timings remained slower than unchanged upstream SVInt, so production arithmetic is unchanged. The inspection below describes the source-review basis; full current-source qualification evidence is being consolidated.

Source inspection, 2026-10-26: slang v12.1, commit `42710c788ce76cd08ce2080773e0b8286c428a05`, `source/numeric/SVInt.cpp` SHA256 `1f30bdd41905a05d4fa3f3628dc45513d0a223fdf6f1906b1473294c110d68e3`. No upstream code was modified. Subsequent [timing results](svint-simd-timings.md) show selective wide mixed-X/Z improvements and small-width regressions. This extends [LIB-02](library-opportunities.md#lib-02), owned by [WP-07](https://github.com/jeffadickson/VeloSim/issues/8).

“AVX128” is interpreted here as 128-bit SIMD using XMM registers, including VEX-encoded operations where available. SSE2 already supplies baseline 128-bit integer operations on x86-64; AVX is not a single 128-bit arbitrary-integer arithmetic facility. Two packed 64-bit additions do not propagate carry between lanes. See [Intel's instruction-extension overview](https://www.intel.com/content/www/us/en/support/articles/000005779/processors.html).

SVInt stores values in 64-bit words, with a separate unknown plane when needed and an inline fast path for known values up to 64 bits. The following are candidate changes inside the pinned upstream implementation, rather than safe optimizations through a public raw-pointer adapter. Preserve the [SVInt API](https://sv-lang.com/classslang_1_1_s_v_int.html) and keep the production VeloSim ABI independent.

| Priority | Source locations in `source/numeric/SVInt.cpp` | Opportunity and limits |
| --- | --- | --- |
| First | `operator&=`, `operator|=`, `operator^=`, `xnor`, lines 1131–1301 | Operate on two adjacent words per XMM vector. Known-value paths are simple AND/OR/XOR; four-state paths also process value/unknown planes. Preserve each branch's exact X/Z truth table and original-value dependencies. |
| First | `flattenUnknowns`, lines 487–498; `operator~`, lines 993–1014 | Vector AND-NOT/XOR for independent word pairs. Keep unknown-plane clearing, final-word masking and `checkUnknown()` representation changes. |
| First | `conditional`, lines 1711–1741 | Parallel value and unknown merge when the condition is unknown. Preserve width/sign extension before entering the loop; load original planes before stores if aliasing is possible. |
| Second | `reductionAnd`, `reductionOr`, lines 926–972; `countXs`, `countZs`, lines 2059–2081 | Vectorize Boolean plane formation and chunk reductions, then finish scalar. SSE2 has no packed population-count instruction; a vector popcount replacement needs a separately justified algorithm/ISA and crossover measurement. |
| Second | `equalsSlowCase`, lines 1860 onward; `exactlyEqual`, near line 2412 | Compare chunks and reduce mismatch flags, but inspect generated code first: existing `memcmp` can already use optimized SIMD. Preserve four-state versus logical equality distinctions. |
| Later | Shift helpers in `SVIntHelpers.h`, lines 113–167 | Adjacent-word shifts can combine shifted vectors, with scalar boundaries. Cross-lane carries, overlapping in-place storage and traversal direction make this more complex than bitwise loops. |
| Low initially | `operator+=`, `operator-=`, `operator*=`, lines 1038–1120; `addGeneral`, `subGeneral`, `mul` in `SVIntHelpers.h`; division at line 2174 onward | Carry/borrow chains and full-width partial products prevent a direct packed-64 replacement. Multiplication/division need a separate algorithm study. Do not expect bitwise SIMD work to fix the earlier multiply-add benchmark. |

Allocation, width coercion and unknown-to-known transitions may dominate small widths. Existing fill/copy operations already use library memory routines in several paths; hand-written intrinsics may regress them. Start with compiler vectorization remarks and disassembly for the current Release build, including alias checks, before introducing intrinsics or a portability dependency.

## Proposed qualification slice

1. Establish scalar/autovectorized baselines and inspect actual assembly. Benchmark bitwise and conditional operations separately from construction, allocation, parsing and serialization, then include an end-to-end workload.
2. Prototype one known-value bitwise loop and one mixed-X/Z loop, retain the scalar path, and compare baseline SSE2 with an isolated AVX-enabled variant. Use unaligned loads unless allocation alignment is proved. Never load a second word past either plane's end; use scalar tails for odd word counts.
3. Test widths 1, 63, 64, 65, 127, 128, 129, 191, 192, 255, 256, 257, 512 and 1024, partial top words, signed/unsigned extension, self-aliasing, all X/Z and mixed per-bit X/Z. Differential-check against the unmodified pinned library and independent truth tables; the existing numeric corpus alone does not qualify conditional, reductions or arbitrary mixed unknown masks.
4. Run sanitizer, negative, property and mutation checks of the changed paths; collect line/branch coverage. Verify dispatch/fallback on supported Windows/Linux targets and retain a portable non-x86 build. AVX execution requires CPU and OS support; do not globally add `-march=native` to distributed SDKs.
5. Retain CPU, compiler, flags, hashes, width/unknown distributions, repetitions, checksums and timings. Adopt only with measured improvement and no semantic regressions. No crossover width, speedup or production SVInt adoption is established by this inspection.

