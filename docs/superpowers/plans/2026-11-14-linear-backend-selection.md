# Configurable schematic linear solvers

The user confirmed keeping the current engine and fixing the LU configuration.
Reuse its existing LU/QR/Faer registry; add no numerical algorithm or dependency.

Implement typed backend selection in the shared session commands. Preserve LU
as the backward-compatible default. Save/reload and parameter-only undo retain
the choice; changing it advances the revision. RC and general DC/AC/transient
requests inherit session selection unless a typed per-run override is supplied.
Reject undefined providers, and validate the native response's actual provider
identity so a silently hard-coded LU request cannot pass Faer verification.

Expose the same selection in browser and MCP, including locked book lessons.
Test malformed configuration, stale commands, history, persistence and native
provider mismatch. Execute RC plus general DC/AC/transient through each backend,
with analytical references and an actual browser/MCP Faer run. Collect coverage,
seeded properties, a selected hard-coded-LU mutation and real StrictDoc parsing.
Keep bulk output on a unique E: child. Preserve other branches and active hosts.
Obtain a fresh final review, commit and push the verified VeloSim checkpoint.

Separate Spice issue47 work: inspect the pinned standard-library File::flush
semantics. Keep raw cargo-mutants outcomes unchanged. If the original survivor is
equivalent on the tested platforms, document the bounded equivalence and retain
sync_all/error handling rather than changing the numerical engine or hiding an
entire function's future mutants. Record requirements and actual evidence in
the Spice checkout; no canonical main merge is part of this task.
