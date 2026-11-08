# Component catalog

The current browser catalog has 90 generic symbols. It follows the upstream distinction between drawing a part and binding a simulation model. This project's names, terminal metadata and original artwork are independently maintained. No RSpice or LTspice component library assets are redistributed.

| Group | Available as generic symbols | Further library work |
| --- | --- | --- |
| Passives | Resistor, capacitor, polarized capacitor, inductor, transformer, coupling directive, saturable inductor and memristor | Coupling and nonlinear model binding |
| Sources | Independent AC/DC and transient waveform symbols, E/F/G/H sources, and behavioral source | Waveform parameter binding outside pulse, and behavioral expressions |
| Semiconductors | Diode, BJT including substrate and thermal variants, bulk/SOI/VDMOS MOSFETs, JFET and MESFET | Model-specific execution beyond the qualified bridge profiles |
| Analog | Voltage-, current- and expression-controlled switches, ideal and five-pin op amps | Switch and op-amp model execution |
| Sheet | Ground, voltage and loop probes, interface port and cell placeholder | Hierarchical editing, dynamic ports and bus entry |
| Transmission | Lossless, lossy and coupled lines, RF port | Transmission and RF model execution |
| Mixed signal | Analog math, limiter, integrator, differentiator, digital logic, sequential blocks and bridges | Code-model and event execution |
| Vendor parts | | User-provided symbols and model bindings, with provenance and verified port order |

The divider uses the resistor, DC voltage source, ground and probe. The native RC profile uses the resistor, ordinary or forward-oriented polarized capacitor, pulse voltage source, ground and probe. The polarized capacitor uses ideal capacitance in this profile. It has no vendor model binding or reverse-bias behavior. Other symbols are available for placement and wiring. The catalog records terminal order independently of drawing geometry and the inspector exposes readiness to the learner.

RSpice groups built-in components by purpose, and LTspice has both generic primitives and installed vendor-specific symbols. LTspice uses SpiceOrder metadata in its symbol files. Treat vendor files as user-supplied inputs with explicit permission before considering redistribution. [RSpice component definitions](https://github.com/JaimeHW/RSpice/blob/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-design/src/schematic/component_type.rs), [Analog Devices schematic reference](https://analogdevicesinc.github.io/ltspice-reference/ai_ref/SCHEMATIC-REFERENCE.html).

## Drawing palette scope

The palette has an entry corresponding to each of the 85 generic component
categories in the pinned [component inventory](https://github.com/JaimeHW/RSpice/blob/908a81b40fd8/crates/rspice-design/src/schematic/component_type.rs).
Five additional entries preserve the existing voltage probe, polarized
capacitor, five-pin op amp and two-terminal F/H symbols. Symbol art is
original. Fixed terminal order is available to the browser and MCP.
The coupling directive is a zero-terminal object. Its winding references,
dynamic cell ports, hierarchy and waveform assets require additional
editor semantics. A palette entry does not establish simulation support.

## Executable catalog expansion

The general bridge compiles the typed editor's nets into the native JSON
request. It supports RLC, independent voltage and current sources, linear
E/F/G/H controlled sources, diode junction charge, MOS1 channel with
explicit overlap and body depletion, three-terminal BJT conduction and
bulk BSIM3 3.3.0 with the configured provider. Availability is bounded
by the native public API and qualification fixtures. Intrinsic MOS1
charge, broader BJT and BSIM profiles, and additional models require
separate bindings and evidence.

| Scope | Capability to qualify |
| --- | --- |
| Primitive circuit | Ground-first RLC and linear sources are compared in DC operating point, AC and transient on the mixed reference fixture. |
| Independent and controlled sources | Complex AC phasors, transient pulses and current-sense bindings run. Other waveform and behavioral forms remain open. |
| Semiconductor and special models | The verified semiconductor subset is recorded in bridge-verification.md. JFET, SOI and further compact model families remain open. |
| Vendor parts | Resolve user-supplied symbol and model libraries with explicit port mapping, provenance and redistribution terms. |

Reference scope comes from the [RSpice technical catalog](https://rspice.app/reference)
and [Analog Devices SPICE quick reference](https://www.analog.com/media/en/news-marketing-collateral/solutions-bulletins-brochures/ltspice-keyboard-shortcuts.pdf).
The sources distinguish generic primitives, compact models and supplied
library assets. The current count measures generic symbols only.

SCH-CAT-001 traces to Core/PartCatalog.cs, Core/Board.cs and Web/Lesson.razor. CatalogTests validates named terminals, unique instance IDs, shape definitions and representative SPICE ordering. BrowserSmoke checks search, placement, the MOSFET bulk terminal and simulation readiness.
