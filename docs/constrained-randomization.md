# Constrained randomization

User-requested product requirement **VS-RANDOM-CONSTRAINT-001** is Draft in [StrictDoc](../requirements/velosim.sdoc), tracked by [WP-16 / issue #17](https://github.com/jeffadickson/VeloSim/issues/17). Implementation remains open. Detailed acceptance below is inferred and must be refined into explicitly supported language subsets.

Provide SystemVerilog constrained randomization in staged slices, documenting supported `rand`/`randc`, constraint blocks, inline constraints, constraint enablement, ordering and distribution constructs. Unsupported syntax must receive explicit diagnostics rather than silently weakening a constraint. Class lifetime support and UVM compatibility are separate prerequisites and claims.

For supported subsets, successful calls must satisfy active constraints with correct declared widths and signedness. Define seed/state replay and failure behavior, including destination updates. Separate unsatisfiable constraints from solver timeout, unknown and infrastructure errors; none of those outcomes may masquerade as a successful randomization. Specify random-stability scope and distribution expectations rather than assuming a solver model is an appropriately random sample.

## Library information: Z3

[Z3](https://github.com/Z3Prover/z3) is the informational candidate in [LIB-13](library-opportunities.md#lib-13), not a mandatory or adopted dependency. Its bit-vector solving and native/.NET interfaces may handle constraint solving while VeloSim owns SystemVerilog translation, object state, seeded selection and diagnostics. Review a pinned version's license, native deployment, resource limits, incremental context lifecycle and binding/AOT compatibility before adoption. Solver satisfiability does not establish translation correctness or uniform model selection.

## Planned acceptance

- Independently enumerate small domains to compare legal solutions and UNSAT results, including width truncation, signed comparisons, overflow, contradictory constraints and boundary values.
- Replay seeds and complete random state under the declared reproducibility scope. Test failure atomicity and unsupported constructs.
- Verify each accepted ordering/distribution and `randc` slice against its own contract; use statistical checks with declared tolerances where applicable, without treating finite samples as proof of uniformity.
- Exercise solver timeout/unknown/cancellation and packaging failures; retain minimized cases and diagnostics.
- Use pinned reference-simulator comparisons where available, property/negative/mutation tests and simulator line/branch coverage. Record exact subset, commands, hashes, seeds and unmeasured areas. No solver or new simulation tests were run for this requirements-only addition.
