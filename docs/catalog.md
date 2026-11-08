# Component catalog

The current browser catalog has 20 generic symbols. It follows the upstream distinction between drawing a part and binding a simulation model. This project's names, terminal metadata and original artwork are independently maintained. No RSpice or LTspice component library assets are redistributed.

| Group | Available as generic symbols | Further library work |
| --- | --- | --- |
| Passives | Resistor, capacitor, polarized capacitor, inductor | Coupled inductors, transmission lines and model-specific passives |
| Sources | DC voltage and current, pulse voltage, VCVS, VCCS | Waveform families, current-controlled sources and behavioral expressions |
| Semiconductors | Diode, NPN, PNP, four-terminal NMOS and PMOS, N- and P-channel JFET | Extra-terminal BJT variants, SOI, VDMOS, MESFET and model-specific parameters |
| Analog | Voltage-controlled switch and five-pin op amp | Current-controlled switches, additional analog primitives and vendor subcircuits |
| Sheet | Ground, voltage probe | Hierarchical ports and bus entry |
| Mixed signal | | XSPICE and digital families |
| Vendor parts | | User-provided symbols and model bindings, with provenance and verified port order |

The divider uses the resistor, DC voltage source, ground and probe. The native RC profile uses the resistor, ordinary or forward-oriented polarized capacitor, pulse voltage source, ground and probe. The polarized capacitor uses ideal capacitance in this profile. It has no vendor model binding or reverse-bias behavior. Other symbols are available for placement and wiring. The catalog records terminal order independently of drawing geometry and the inspector exposes readiness to the learner.

RSpice groups built-in components by purpose, and LTspice has both generic primitives and installed vendor-specific symbols. LTspice uses SpiceOrder metadata in its symbol files. Treat vendor files as user-supplied inputs with explicit permission before considering redistribution. [RSpice component definitions](https://github.com/JaimeHW/RSpice/blob/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-design/src/schematic/component_type.rs), [Analog Devices schematic reference](https://analogdevicesinc.github.io/ltspice-reference/ai_ref/SCHEMATIC-REFERENCE.html).

## Executable catalog expansion

The existing typed editor and native solver are the reuse path for expanding
coverage. The current bridge sends a fixed RC request. Extending this bridge
to netlist a typed circuit and dispatch supported device and analysis
combinations is a prerequisite for broad catalog execution. For each
family, track symbol and terminal order, instance parameters, model binding,
analyses and a reference case separately. The existing device, source,
behavioral, digital and compact-model StrictDoc catalogs hold those cases.

| Scope | Capability to qualify |
| --- | --- |
| Primitive circuit | Compile shared nets and ground into a native request, then check arbitrary RLC with DC, AC and transient analyses. |
| Independent and controlled sources | Map waveform parameters and current-sense branches, then compare signed DC, AC phase, breakpoints and gain. |
| Semiconductor and special models | Bind exact model families and levels, qualify terminals and parameter defaults, and compare representative DC, AC and transient decks per family. |
| Vendor parts | Resolve user-supplied symbol and model libraries with explicit port mapping, provenance and redistribution terms. |

Reference scope comes from the [RSpice technical catalog](https://rspice.app/reference)
and [Analog Devices SPICE quick reference](https://www.analog.com/media/en/news-marketing-collateral/solutions-bulletins-brochures/ltspice-keyboard-shortcuts.pdf).
The sources distinguish generic primitives, compact models and supplied
library assets. The current count measures generic symbols only.

SCH-CAT-001 traces to Core/PartCatalog.cs, Core/Board.cs and Web/Lesson.razor. CatalogTests validates named terminals, unique instance IDs, shape definitions and representative SPICE ordering. BrowserSmoke checks search, placement, the MOSFET bulk terminal and simulation readiness.
