# Dafny Formal Verification Suite

VeloSim maintains a machine-checked Dafny formal specification and proof suite under
`formal/dafny/` covering the core mathematical invariants introduced across Verilator
Gaps Waves 1–4. Every module is verified with **zero `assume` statements, zero `{:axiom}`
attributes, zero warnings, and zero errors**.

## Toolchain & Reproducibility

- **Verifier:** Dafny `4.11.0` (`4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2`)
- **SMT Solver:** Z3 `4.15.5`
- **Command:**

```bash
for f in formal/dafny/*.dfy; do
  dafny verify "$f" --solver-path "$(command -v z3)" --verification-time-limit 20
done
```

## Verified Modules & Proof Obligations

| Module | Target Implementation | Verified Obligations | Key Proven Theorems |
|---|---|---:|---|
| `formal/dafny/FourStateResolution.dfy` | `native/rtl/runtime.h`, `src/VeloSim.Core/RtlCSharpEmitter.cs`, `src/VeloSim.Core/RtlRustEmitter.cs` | 25 | Canonical 4-state preservation (`ResolvePairPreservesCanonical`, `ResolveFinalizePreservesCanonical`), driver-order commutativity (`ResolvePairCommutative`), reduction associativity (`ResolvePairAssociative`), idempotence (`ResolvePairIdempotent`), high-impedance Z identity (`ResolvePairZIdentity`), exact IEEE 1800-2017 Tables 6-1/6-2/6-3 + `tri0`/`tri1` pull-down/pull-up (`Ieee1800TablesExact`), and arbitrary-width IEEE 1800 §11.4.6 wildcard equality (`WildEqProperties`). |
| `formal/dafny/FourStateW4Equivalence.dfy` | `native/rtl/runtime.h` (`vsrtl::w4`), `src/VeloSim.Core/RtlNarrow64.cs`, `src/VeloSim.Core/RtlCSharpEmitter.cs` (`W4`/`W4Ops`), `src/VeloSim.Core/RtlRustEmitter.cs` (`W4`/`w4`) | 29 | Exact total bijection between 16-byte IEEE 1800 `aval`/`bval` `W4` and canonical 3-plane `Bit4`/`Word4` (`W4BitBijection`, `W4WordBijection`), plus exact bit-lane and arbitrary-width operator homomorphism for `BNot`, `BAnd`, `BOr`, `BXor` (`W4BitwiseEquivalence`), unknown-selector ternary merge including `Z`/`Z` preservation (`W4CondMergeEquivalence`), 4-state logical equality (`W4EqEquivalence`), all 5 IEEE 1800 net kinds & 2-instruction `tri0`/`tri1` finalize (`W4ResolveEquivalenceBit`, `W4ResolveEquivalenceWord`), and wildcard equality (`W4WildEqEquivalence`). |
| `formal/dafny/DagLevelizer.dfy` | `src/VeloSim.Core/RtlCombinational.cs`, `native/rtl/thread_pool.h` | 16 | Same-level RAW/WAR/WAW hazard freedom (`SameLevelHasNoHazards`), pairwise same-level commutation (`SameLevelPairCommutes`), and termination, topological soundness, and fail-closed cycle rejection of executable Kahn-style level assignment (`ComputeLevels`). |
| `formal/dafny/SvaDelayPipeline.dfy` | `src/VeloSim.Core/RtlVerification.cs`, `RtlCSharpEmitter.cs`, `RtlRustEmitter.cs` | 12 | State-invariant preservation (`StepPreservesValid`, including `Cover ==> vacuous == 0`), `disable_iff` pipeline flush & non-failure (`DisableIffClearsAllPending`), exact temporal shift-register equivalence across arbitrary disables and overlapping antecedents for delays `0..64` (`ShiftRegisterExactTemporalSemantics`), and 0-delay counter conservation (`ZeroDelayAccountingConservation`). |
| `formal/dafny/CoverageMergeInvariants.dfy` | `src/VeloSim.Core/RtlCoverageMerge.cs`, `native/rtl/vpi.cpp` | 14 | Fail-closed schema/model/width/property rejection (`MergeIsFailClosed`), merge commutativity (`MergeIsCommutative`), merge associativity (`MergeIsAssociative`), counter and toggle-mask monotonicity (`MergeIsMonotonic`), and IEEE 1800 §19.5.4/19.5.5 `illegal_bins` > `ignore_bins` > normal > `default` sampling priority (`CoverpointSamplingPriority`). |
| **Total** | | **96** | **96 verified, 0 errors, 0 warnings** |

## Scope & Proof Boundary

These Dafny proofs establish the mathematical soundness of VeloSim's **algorithmic
specifications** — not mechanical equivalence of C++, C#, or Rust compiler code generation
or OS thread scheduling:

- `FourStateResolution.dfy` proves the exact per-bit boolean formula lifted lane-by-lane to
  arbitrary-width `Word4 = seq<Bit4>`, covering both narrow 64-bit (`w64`) and wide multi-word
  nets. Equivalence between the C++/C#/Rust 64-bit bitwise operations (`&`, `|`, `~`, `^`) and
  lane-by-lane boolean operations relies on standard bitwise semantics of unsigned 64-bit integers.
- `DagLevelizer.dfy` proves that the levelizer's output partitions any acyclic combinational graph
  into hazard-free commuting levels and rejects cycles. Thread-pool memory visibility and barrier
  synchronization in `native/rtl/thread_pool.h` are validated empirically via C++ native tests
  (`tests/native/rtl_runtime_tests.cpp`) and end-to-end Wave 4 tests (`RtlVerilatorGapsWave4Tests.cs`).
- `SvaDelayPipeline.dfy` and `CoverageMergeInvariants.dfy` model the shift-register state machine,
  coverpoint sampling priority, and report merge algebra; JSON serialization, atomic C++/C#/Rust
  rollback on assertion failure, and CLI/file-I/O error handling are verified by the unit and
  integration test suites.
