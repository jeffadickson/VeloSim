# Checkpoint 020 peer review

Independent simulation and tooling AI reviewers inspected the managed port and interop.

Simulation findings: managed IR accepted multibit reset signals rejected by the native ABI; `--release-reset` never asserted reset and therefore left newly lowered unknown logic unknown. Fixes require consistent schema validation and a reset-assert/release regression starting from normalized HDL IR.

Tooling findings: subprocess deadlines stopped at direct-child exit, permitting descendant-held pipes to block forever; already-cancelled calls still launched processes. Fixes require cancellation checks before launch and deadlines covering output draining.

Root integration found CMake can reset VELOSIM_COVERAGE when the compiler changes. The build helper now reconfigures and verifies instrumentation instead of accepting an uninstrumented report. All checkpoint-020 findings were resolved and regression-tested before the025 local commit.

Coverage integration also rejected an initially empty managed report from the old collector. Updated the collector/Test SDK and explicitly selected VSTest in accordance with the [Coverlet package documentation](https://www.nuget.org/packages/coverlet.collector/10.1.0). Nonempty managed instrumentation is now a required coverage acceptance check.
