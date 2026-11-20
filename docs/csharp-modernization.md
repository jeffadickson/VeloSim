# Released .NET 11 and schematic modernization

Maintained projects target `net11.0` with stable SDK 11.0.100, C# 15 and analysis
level 11. The root SDK selector rejects prereleases while allowing stable patch
roll-forward. Existing NativeAOT compiler parallelism remains bounded. Historical
reports retain the frameworks and metrics they actually measured.

| Capability | Use and reason |
|---|---|
| C# 15 unions and exhaustive switches | The schematic startup command has serve, help and invalid cases. The compiler checks host/test switches when that finite contract changes; old UI/MCP wire result types are preserved. |
| C# 15 collection constructor arguments | Startup option identity and the VeloSim native-module cache keep explicit ordinal comparers. Constructor semantics are retained rather than accidentally falling back to a different comparer. |
| Source-generated System.Text.Json | New CLI failures use a generated JSON context, with stable lower-case property naming. Other serialization paths keep their existing implementation; no whole-application AOT claim follows. |
| Existing C# 11–14 idioms | Raw strings, required/init-only data, pattern matching, collection expressions, primary constructors, generated regex and nullable analysis remain available where the current code already uses them. No mechanical rewrite is necessary to gain those capabilities. |
| Framework/runtime upgrade | Existing async, span, interop and JSON code runs on .NET 11. Compiler/runtime optimization availability is not a measured application speedup. |

The new standalone apphost resolves installed-relative assets, with explicitly
bounded startup arguments and no public-listener override. The browser and MCP
still use the existing typed command layer, revisions and independent solver
adapter. Self-contained publication removes an installed-runtime requirement;
it is ordinary native-apphost/JIT publication, not a claimed NativeAOT build.

Features not selected include replacing public command/result contracts with
unions, memory-safety preview switches, generic numerical type rewrites and
wholesale replacement of the existing bounded process runner with new convenience
APIs. Those changes would require distinct compatibility/numerical evidence.

During verification an invalid even FsCheck replay gamma caused an unhandled
exception while the adapter returned a nominally successful partial TRX report.
That run is invalid evidence, retained under `invalid-seed-report.trx` in the
artifact directory. The corrected replay is `110011,2026111201` (128 cases).
The reusable test entry point now rejects unhandled/fatal reports and missing
required cases. This is test-runner integration hardening, not a simulator bug.

Primary references: [C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15),
[.NET 11](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview),
[System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation).
