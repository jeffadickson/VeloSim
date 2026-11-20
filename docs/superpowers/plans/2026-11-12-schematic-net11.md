# Schematic integration and .NET 11 implementation plan

> For agentic workers: use the executing-plans workflow for these sequential tasks.

User outcomes: SchematicLab is a project inside VeloSim and also a separately
runnable, browser-based local application. Move maintained C# projects to released
.NET 11, preserve the schematic history and typed UI/MCP command contracts, and
verify before publishing Git checkpoints. Do not imply new HDL/analog co-simulation.

## Existing design and reuse

Reuse the complete 75-file SchematicLab repository at source commit 02c02ad,
including Core, Solver, Web, WebHost, Mcp, tests and stable StrictDoc IDs. Import
under apps/SchematicLab with its history as the second parent of a merge; do not
delete the original checkout or substitute a submodule. Core remains independent
of Blazor and the HDL runtime. The solver adapter speaks the existing JSON process
protocol to an explicitly selected native simulator. UI and MCP continue to use
the same command registry and revision checks.

Microsoft's .NET 11 and C# 15 release documentation is the dependency reference.
Use released SDK 11.0.100, not the installed 11.0.100-rc.3.26522.148. Install and
verify the release before using the registered RC uninstaller. Preserve SDKs 8/10.
Use framework facilities and existing build helpers; no new parser, UI framework
or solver implementation is needed. New collection constructor arguments can
preserve explicit comparer/capacity semantics. Audit other modern constructs and
adopt only useful, behavior-preserving changes. Do not change public result shapes
just to demonstrate union syntax. No Dafny proof is claimed: the packaging path
boundary uses established Path/Zip APIs rather than a new arithmetic algorithm.

## Tasks and acceptance

1. Verify exact imported Git blobs and run the existing Core regression suite.
   Commit the history-preserving import separately after verification.
2. Update global.json, root/app target frameworks, package locks, build helper
   framework-specific paths, examples and documented commands to .NET 11. Audit
   analyzers and source-level modernization; record adopted and deferred features.
   Build/test the managed solution, with native/platform skips explicit.
3. Add an independent schematic solution and artifact-aware publish/launch path.
   Pack browser assets beside WebHost; default root resolution must work without
   the VeloSim checkout. Include a trusted solver only through an explicit path,
   retain its SHA-256/provenance, and preserve explicit unavailable-model errors.
   A portable standalone bundle is a local web application, not a native GUI port.
4. Test missing assets, invalid arguments, standalone default paths, stale edits,
   RC probe and waveform, and MCP from the packaged layout. Run the browser
   smoke/headless rendering when the local computer-control bridge is unavailable.
5. Run real StrictDoc parsing, scoped line/branch coverage and relevant targeted
   negative/property/mutation checks. Retain exact commands, source/tool hashes,
   counts, seeds and unavailable coverage. Review and push verified checkpoints
   using the established repository CI policy; no manual workflow dispatch.

All downloads, intermediate outputs, logs, temp and packages go under the unique
E: artifact run. E: is exFAT; native Linux filesystem requirements must be checked
rather than assumed. SDK system installation is a bounded required C: footprint;
check free space first. Every heavy job needs an armed process-tree/aggregate
memory guard, bounded concurrency, OS headroom and absolute timeout/cleanup.
