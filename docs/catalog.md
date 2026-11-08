# Component catalog

The current browser catalog has 22 generic symbols. It follows the upstream distinction between drawing a part and binding a simulation model. This project's names, terminal metadata and original artwork are independently maintained. No RSpice or LTspice component library assets are redistributed.

| Group | Available as generic symbols | Further library work |
| --- | --- | --- |
| Passives | Resistor, capacitor, polarized capacitor, inductor | Coupled inductors, transmission lines and model-specific passives |
| Sources | DC voltage and current, pulse voltage, VCVS, VCCS, CCCS, CCVS | Additional waveform families and behavioral expressions |
| Semiconductors | Diode, NPN, PNP, four-terminal NMOS and PMOS, N- and P-channel JFET | Extra-terminal BJT variants, SOI, VDMOS, MESFET and model-specific parameters |
| Analog | Voltage-controlled switch and five-pin op amp | Current-controlled switches, additional analog primitives and vendor subcircuits |
| Sheet | Ground, voltage probe | Hierarchical ports and bus entry |
| Mixed signal | | XSPICE and digital families |
| Vendor parts | | User-provided symbols and model bindings, with provenance and verified port order |

The divider uses the resistor, DC voltage source, ground and probe. The native RC profile uses the resistor, ordinary or forward-oriented polarized capacitor, pulse voltage source, ground and probe. The polarized capacitor uses ideal capacitance in this profile. It has no vendor model binding or reverse-bias behavior. Other symbols are available for placement and wiring. The catalog records terminal order independently of drawing geometry and the inspector exposes readiness to the learner.

RSpice groups built-in components by purpose, and LTspice has both generic primitives and installed vendor-specific symbols. LTspice uses SpiceOrder metadata in its symbol files. Treat vendor files as user-supplied inputs with explicit permission before considering redistribution. [RSpice component definitions](https://github.com/JaimeHW/RSpice/blob/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-design/src/schematic/component_type.rs), [Analog Devices schematic reference](https://analogdevicesinc.github.io/ltspice-reference/ai_ref/SCHEMATIC-REFERENCE.html).

## Executable catalog expansion

The general bridge compiles the typed editor's nets into the native JSON
request. It supports RLC, independent voltage and current sources, linear
E/F/G/H controlled sources, diode conduction and an explicit MOS1 channel
binding. Availability is bounded by the native public API and qualification
fixtures. The diode and MOS1 profiles cover conduction. Junction charge,
MOS terminal charge and advanced families require separate bindings and
evidence. The existing StrictDoc catalogs track those cases.

| Scope | Capability to qualify |
| --- | --- |
| Primitive circuit | Ground-first RLC and linear sources are compared in DC operating point, AC and transient on the mixed reference fixture. |
| Independent and controlled sources | Complex AC phasors, transient pulses and current-sense bindings run. Other waveform and behavioral forms remain open. |
| Semiconductor and special models | Conduction diode and MOS1 channel are the first bindings. BJT, JFET and BSIM families need public solver contracts and reference decks. |
| Vendor parts | Resolve user-supplied symbol and model libraries with explicit port mapping, provenance and redistribution terms. |

Reference scope comes from the [RSpice technical catalog](https://rspice.app/reference)
and [Analog Devices SPICE quick reference](https://www.analog.com/media/en/news-marketing-collateral/solutions-bulletins-brochures/ltspice-keyboard-shortcuts.pdf).
The sources distinguish generic primitives, compact models and supplied
library assets. The current count measures generic symbols only.

SCH-CAT-001 traces to Core/PartCatalog.cs, Core/Board.cs and Web/Lesson.razor. CatalogTests validates named terminals, unique instance IDs, shape definitions and representative SPICE ordering. BrowserSmoke checks search, placement, the MOSFET bulk terminal and simulation readiness.
