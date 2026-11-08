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

## Semiconductor checkpoint

Run the same oracle command with a fresh empty output directory and append
`diode` or `mos1`. The fixture is a resistor-loaded diode at P1 and a
resistor-loaded NMOS level 1 channel at P2. Both browser and MCP ran all
three analyses for both probes. The browser result view was inspected.

| Probe | DC error | Maximum AC component error | Maximum transient error |
| --- | ---: | ---: | ---: |
| P1 diode | 1.28105988e-7 V | 2.22408265e-7 V | 5.53986483e-5 V |
| P2 NMOS | 2.64602207e-9 V | 8.73991879e-10 V | 2.63112365e-6 V |

Each probe used one DC point, two AC frequencies, and 1,001 native
transient samples to 1 ms. The native nonlinear solver required backward
Euler, so the bridge selects it automatically. The initial diode comparison
on the coarser 1 us ngspice grid exceeded 0.5 mV after interpolation.
Refining the ngspice maximum step to 0.25 us produced 4,011 points and
passed the original tolerance. DC and AC used 10 microvolts for both
semiconductor observables. These examples cover conduction at the
demonstrated biases. They do not measure junction storage, intrinsic MOS
charge, PMOS behavior, BJT or BSIM.

The final Core suite passed 28 tests. Coverlet counted 3,486 of 3,949 lines
and 957 of 1,438 branches, including generated code. An isolated mutant
that mapped the gate to the bulk terminal was caught. StrictDoc parsed
12 documents with 247 unique IDs.

## Diode depletion and diffusion

The 2 nF CJO and 10 ns TT example passed browser, MCP and the independent
ngspice 47+ comparison. Maximum probe errors were 0.128 microvolts in DC,
0.222 microvolts for two complex AC points, and 0.359 mV for 2,001 native
samples against 4,011 ngspice samples. Transient used 50 ns maximum native
step and 25 ns maximum oracle step. The tolerance was 0.5 mV.

The Core suite passed 29 tests. Coverlet measured 3,494 of 3,957 lines and
976 of 1,458 branches, including generated code. An isolated grading-boundary
mutant was caught. Browser, MCP, transport and Rust coverage were unmeasured.
See E:/Artifacts/SchematicLab-charge-a5c5329 for local logs and screenshots.

## MOS1 explicit charge

The NMOS fixture with nonzero overlap and body depletion passed browser,
MCP and ngspice. DC error was 0.607 nV, maximum complex AC component error
1.435 nV, and maximum transient error 0.175 mV (2,001 native and 4,011
reference samples). The first, higher-bias fixture hit the native Newton
limit. The verified fixture uses a 1 V drain supply and zero GAMMA.
This scope excludes intrinsic channel charge and a PMOS reference fixture.

The Core suite passed 30 tests. Coverlet measured 3,503 of 3,966 lines and
991 of 1,474 branches. Native, transport, MCP and browser code were unmeasured.
