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

## Three-terminal BJT

The bridge reuses the Ebers–Moll primitive composition used by the native
deck frontend. The NPN collector fixture measured errors of 3.19 nV in DC,
7.34 nV for two complex AC points, and 1.03 microvolts in transient
(2,001 native and 4,011 ngspice samples). The reference uses the nominal
27 C thermal-voltage convention. Browser and MCP ran all three modes.
PNP polarity is mapped but lacks a new independent fixture. This is
conduction only, with no transistor storage or high-current effects.

Core passed 31 tests. Coverage was 3,551 of 4,014 lines and 1,030 of 1,524
branches. The targeted base-to-emitter terminal mutant was caught.

## Bulk BSIM3 3.3.0

The loaded NMOS fixture passed browser, MCP and ngspice for DC, AC at
1 MHz and 1 GHz, and a 1 ns gate ramp with a 2 ps native maximum step.
Maximum drain probe errors were 1.087 pV in DC, 19.85 pV in complex AC
and 6.132 microvolts in transient (501 native and 1,008 reference points).

Configure SPICE_SOLVER_PATH with a BSIM3-capable native executable, and
SPICE_BSIM330_HOST and SPICE_BSIM330_MODEL with trusted absolute paths
before starting either host. The tested Windows x64 executable, adapter
and module SHA-256 hashes were, respectively:

- 96CA8B0FF7F4091F32867EADDE25D6E34583351CDDA220D26F36C1778A439B65
- E2DAAE9246A498EF3A753E9364356C01CD8721FD5C49D9E6EC4160EF13F23E56
- 57DC3BEBE54A95B36723B3B1E1B543BA01B32A7C9C9A00524B966B3459161608

The oracle takes the fixture name bsim3 and an empty artifact directory.
This checkpoint does not verify PMOS or Linux through SchematicLab.
Core passed 32 tests and recorded 3,592 of 4,055 lines and 1,080 of
1,576 branches, including generated code.

## Extended drawing palette

Core placed, saved and reloaded each of the 90 symbol kinds and checked
ordered pins and the zero-terminal coupling directive. Browser searched
for and placed the file-backed PWL current symbol, verified its polarity
terminal and drawing-only status, and repeated all four family demos.
The rendered symbol was inspected. MCP enumerated 90 entries, checked
the coupled-line terminal count and current source prefix, and ran the
same model demos. The targeted source-prefix mutant was caught, as
were the prior MOS charge and BSIM3 geometry boundary mutants.

Final Core verification passed 33 tests. Coverlet measured 3,689 of
4,152 lines and 1,092 of 1,588 branches, including generated code.
StrictDoc parsed 12 documents and 252 unique requirement IDs. Browser,
MCP, transport, provider and native solver were exercised without
coverage instrumentation. Full mutation and fuzz campaigns were not run.

## BJT storage and inline source waveforms

The charged NPN fixture has a 500 ohm base resistor, 300 pF base-emitter
and 200 pF base-collector capacitances, and 1 ns forward and 2 ns reverse
transit times. DC, AC and transient agreed with ngspice within 4.15 nV,
20.9 nV and 0.389 mV, respectively. Browser and MCP ran the same profile.

The three divider fixtures exercised PWL, damped SIN and EXP. Maximum
voltage errors were 1.12e-16 V, 0.122 microvolts and 0.252 mV. The EXP
fixture uses an explicit nonzero rise delay because the oracle defaults
its zero delay to the transient output step. A separate mixed RLC PWL/SIN
trial failed the native L1 constraint audit and remains unqualified.

Core passed 34 tests with 3,736/4,205 lines and 1,140/1,674 branches
covered including generated code. The duplicate-PWL-time mutant was
caught. Browser, MCP and native engine coverage were unmeasured.

## Pinned FD-SOI DC

The Windows x64 native provider ran a 1 kΩ loaded FD NMOS with an
independent −1 V back gate. Browser and MCP completed DC and rejected
AC. Core also checked the floating body-contact requirement. The drain
probe differed from the pinned ngspice deck by 9.39e-15 V.

Set SPICE_BSIM_SOI_FD_HOST to the trusted native FD host and use a
compatible executable. For the oracle, set SCHEMATIC_SOI_NGSPICE_CARD
to the exact pinned card and run the fd-soi fixture. The tested
engine and FD library hashes were 2FABE6FE59455B3A60251138853660F7190A5FAEB1B71FB9F0E0CCF378C284FD
and 573D6E3C4C1573EC54DDCB973960220185A49C92C8A0B85CD3A5089508815B1C.
Core passed 35 tests with 3,773/4,243 lines and 1,178/1,712 branches.

## Higher-bias DC recovery and settings

The original 3 V drain-supply and 1 V diode-drive MOS1 fixture failed
with the default Newton iteration limit in DC and AC. Its native
one-shot request succeeded with source continuation in DC. The browser
and MCP opt-in recovered that operating point, which matched ngspice
within 2.65 nV. AC still uses the default operating-point initialization.

The browser and MCP restored the PWL and charged-BJT entry fields from
a saved sheet. The isolated mutant dropping settings restoration failed.
Core passed 36 tests with 3,848/4,331 lines and 1,202/1,756 branches
covered. Browser, MCP, adapter and native solver coverage were unmeasured.
