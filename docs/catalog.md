# Component catalog

The current browser catalog has 18 generic symbols. It follows the upstream distinction between drawing a part and binding a simulation model. This project's names, terminal metadata and original artwork are independently maintained. No RSpice or LTspice component library assets are redistributed.

| Group | Available as generic symbols | Further library work |
| --- | --- | --- |
| Passives | Resistor, capacitor, inductor | Polarized variants, coupled inductors, transmission lines and model-specific passives |
| Sources | DC voltage and current, VCVS, VCCS | Waveform families, current-controlled sources and behavioral expressions |
| Semiconductors | Diode, NPN, PNP, four-terminal NMOS and PMOS, N- and P-channel JFET | Extra-terminal BJT variants, SOI, VDMOS, MESFET and model-specific parameters |
| Analog | Voltage-controlled switch and five-pin op amp | Current-controlled switches, additional analog primitives and vendor subcircuits |
| Sheet | Ground, voltage probe | Hierarchical ports, bus entry and named nets |
| Mixed signal | | XSPICE and digital families |
| Vendor parts | | User-provided symbols and model bindings, with provenance and verified port order |

The resistor, DC voltage source, ground and probe participate in the browser divider. Every other entry can be placed and wired. It refuses analytical evaluation when those entries are present. The catalog records terminal order independently of drawing geometry and the inspector exposes readiness to the learner.

RSpice groups built-in components by purpose, and LTspice has both generic primitives and installed vendor-specific symbols. LTspice uses SpiceOrder metadata in its symbol files. Treat vendor files as user-supplied inputs with explicit permission before considering redistribution. [RSpice component definitions](https://github.com/JaimeHW/RSpice/blob/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-design/src/schematic/component_type.rs), [Analog Devices schematic reference](https://analogdevicesinc.github.io/ltspice-reference/ai_ref/SCHEMATIC-REFERENCE.html).

SCH-CAT-001 traces to Core/PartCatalog.cs, Core/Board.cs and Web/Lesson.razor. CatalogTests validates named terminals, unique instance IDs, shape definitions and representative SPICE ordering. BrowserSmoke checks search, placement, the MOSFET bulk terminal and simulation readiness.
