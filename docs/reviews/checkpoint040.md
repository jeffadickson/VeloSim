# Checkpoint 040 peer review

Both independent AI reviewers found a replay binding defect: recorded tool hashes were checked, but nullable tool configuration could resolve a different Surelog/normalizer from the environment during replay. Effective executable paths must be persisted and bound to the recorded stage fingerprints. Relative executables must resolve against their recorded working directory.

Simulation review additionally required validation of recorded differences and semantic labels against recomputed results. Tooling review required preserving captured timeout output in command metadata.

Root verified an alternative Verilator CMake integration using Ninja and bounded parallel builds. The existing --binary shortcut's make build is being replaced in041. Generated CMake must quote paths safely and test directories with spaces.

Before these fixes, NUnit31/31, a real100-cycle three-engine run, report inspection and replay passed. Added adversarial tests are required before accepting the040 findings as resolved.

Resolution confirmed at050: exact frontend/tool-role binding, semantic/difference validation, captured timeout output and quoted Ninja paths implemented. Actual1000-cycle NativeAOT three-engine replay and space-path builds pass; peer050 follow-up accepted replay binding fixes.
