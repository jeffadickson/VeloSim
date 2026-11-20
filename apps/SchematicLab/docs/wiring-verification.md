# Wiring and persistence verification

Tested the wiring change based on `53cc829` on Windows with .NET SDK
10.0.403, Coverlet 10.1.0, Node 24.20.0 and Chromium headless. Bulk
outputs are in `E:/Artifacts/SchematicLab-wiring-fa13ea27`.

| Evidence | Result |
| --- | --- |
| `dotnet test Tests --collect "XPlat Code Coverage"` | 20 passed, 0 failed, 0 skipped. Includes explicit junction connectivity, named RC connections, exact JSON round trip, invalid terminal rejection, and 128 random truncated documents with seed 110821. |
| Coverlet Cobertura | 1,850 of 2,159 lines and 481 of 796 branches in the instrumented Core assembly, including generated serialization code. Web, MCP, and the native solver were exercised without coverage instrumentation. |
| Targeted mutation | Changing case-insensitive label matching to ordinal case-sensitive matching was killed by the named RC probe regression. The isolated run had 1 expected failure and 1 pass. No Stryker campaign ran for this change. |
| `dotnet publish Web` and `node Tests/BrowserSmoke.cjs` | In the embedded browser lesson, a crossing preserved 2.5 V until explicitly joined. The junction survived save/reload. Deletion restored 2.5 V. A labeled RC probe survived a page reload and produced a 502-sample curve ending at 4.9665 V. Screenshot inspected. |
| `dotnet build Mcp` and `dotnet run --file scripts/McpSmoke.cs` | Actual stdio client discovered 25 tools. Verified route and junction edits, exact JSON round trip, stale load rejection, and a native RC waveform with 502 samples and maximum analytic error 4.12527e-5 V. |
| StrictDoc 0.30.4 | Parsed 11 documents with 242 unique requirement IDs. |

The browser and MCP used the existing native solver executable at
`E:/Artifacts/Spice-threeway-20261108T072907/rust/debug/circuit-simulator.exe`.
See [RC verification](rc-verification.md) for the solver setup and
[the README](../README.md) for the host and client smoke commands. This
verification covers terminal labels and explicit joins between two recorded
wire bends. Whole-net highlighting and persistence of view and analysis state
remain open under the broader requirements.
