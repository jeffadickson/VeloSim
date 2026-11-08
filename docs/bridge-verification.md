# General bridge verification

Windows, .NET SDK 10.0.403, Chromium headless, ngspice 47+ and the
existing native solver. Bulk logs are under
`E:/Artifacts/SchematicLab-bridge-78eb0d4`.

| Mixed RLC and controlled sources at P1 | Native points | ngspice points | Maximum component error |
| --- | ---: | ---: | ---: |
| DC operating point | 1 | 1 | 0 V |
| AC at 100 and 1000 Hz | 2 | 2 | 5.55111512e-16 V |
| Trapezoidal transient to 3 ms | 1501 | 1521 | 0.000377092073 V |

The transient uses linear interpolation on the ngspice time grid and a
0.5 mV tolerance. DC and AC use a 1 nV tolerance. The circuit includes
RLC, independent voltage and current sources, and E, F, G and H sources.
Browser and MCP executed all three analyses. The browser screenshot was
inspected. The analytical divider and native RC regressions still pass.

Run `dotnet run --file scripts/BridgeNgspice.cs -- <ngspice> <native> <existing-output-directory>`
for the independently authored reference deck. SHA-256 for ngspice was
`12BC57018AA5142D28065704D09BC6B06DA2A3886F7864CA042ED4824CCF910F`.
The native solver hash was
`CF642C3F234720742794EBADB8DEDC4A6FA6B91640F133BF53EDAAD261699CB5`.

The Core suite passed 27 tests, with 3,432 of 3,896 lines and 869 of 1,350
branches covered, including generated code. It rejected 64 seeded
malformed source rows (seed 110825). The isolated mutation removing
current-control branch IDs was caught. Stryker and coverage for the web,
MCP, transport and native code were outside this run. StrictDoc parsed
12 documents and found 246 unique IDs.
