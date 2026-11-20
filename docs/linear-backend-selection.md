# Schematic linear backend selection

The engine remains Rust. This change removes two hard-coded `backend="lu"`
request fields from the C# schematic adapters. It does not change LU's numerical
algorithm, migrate the engine to C++, or add a new provider dependency.

## Contract and implementation

Core exposes `SolverBackend` (Lu/Qr/Faer), `SetSolverBackend` and
`ReadSolverBackends`. Persisted `BridgeField.Backend` uses canonical `lu`, `qr`
or `faer`. Invalid mixed field imports fail atomically. A selection change
advances the revision and records parameter-only history; it is available in a
locked lesson. Legacy documents default to LU. Current saved RC waveform inputs
retain their own provider and are checked against restored settings.

RC captures the selection in its immutable input. General options inherit the
session selection unless a typed per-run override is supplied. An override does
not alter the default. Native adapters send the selected wire name and require
the expected response `backend.id` before accepting samples. The result retains
its effective choice instead of copying whichever dropdown is currently visible.

| Choice | Required native provider ID | Known analysis support |
|---|---|---|
| Lu | nalgebra_dense_lu_reference | DC, complex AC, transient |
| Qr | nalgebra_dense_qr_reference | DC/transient; AC explicitly refused |
| Faer | faer_sparse_lu | DC, complex AC, transient |

These are the three known adapters in this bridge, not discovery/qualification
of every registered engine extension. LU and QR use the same numerical library;
QR is not a second independently qualified numerical provider. CPU/GPU and model
providers remain separate concerns. There is no silent fallback.

The browser selector and two new MCP tools use the shared commands. The selector
also notifies the parent view so its RC readout respects the changed revision.
General samples display their captured adapter, including a stale-revision note
when appropriate. Browser and stdio MCP remain separate document sessions.

## Numerical evidence

`apps/SchematicLab/scripts/BackendSmoke.cs` exercised the actual native executable
SHA-256 `c3caf38a08fa03c4cb375408d96ddbcf984fe4e1ef19f34b2e3c4fedce46888b`
on Windows. Across all three selections it completed **11 numerical runs** and
one explicit unsupported-QR-AC refusal: RC transient for each, general DC and
transient for each, and complex AC for LU/Faer. The RC fixtures use R=1kΩ, C=1µF;
transient input has a finite linear ramp. The general PULSE source's DC low level
is zero, rather than its 5 V transient high level.

References are independent closed-form charging/ramp equations and the complex
transfer `1/(1+jωRC)` at 100/1000 Hz. Bounds: RC/general transient 0.02 V (existing
RC bound), general DC 1e-6 V, AC 1e-6 V + 1e-4 relative. Actual maximum error was
about 4.126e-5 V for transient and below 1.3e-16 V for AC. No ngspice corpus
benchmark or large/nonlinear-circuit provider qualification was added here.

Two selected mutations restored the old hard-coded LU fields, one in each
request builder. Both failed at the native provider-identity contract; source
bytes were restored and the complete positive native replay passed afterward.
These are two targeted mutations, not a broad Stryker.NET score.

An early harness assumption incorrectly used the PULSE high level for DC and
was corrected to its actual zero bias. Another initial run exposed QR's genuine
lack of complex implementation; the product now advertises/refuses that limit.
The original logs remain local; neither failed attempt is counted as a pass.

Raw evidence: `E:/Artifacts/VeloSim-backends-ff63ff8-20261114`.
The final unit/property coverage, browser/MCP replay, source hashes and package
provenance are recorded in `linear-backend-evidence.json`. Preliminary and reused
build outputs are retained; the final isolated verification is the reported one.
The prior whole-VeloSim Windows baseline retains its separately documented
15 failures/141 skips; this slice does not resolve or relabel them. Linux/macOS,
NativeAOT, optional model providers, WALLY rendering and shared-session agent
control are not qualified by this change.
