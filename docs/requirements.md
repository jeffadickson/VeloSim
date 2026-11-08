# SchematicLab requirements

This index describes the product in its own terms. Each ID maps to a StrictDoc requirement. The verification column describes what must be demonstrated before claiming the complete capability.

| ID | Required behavior | Verification |
| --- | --- | --- |
| SCH-WEB-001 | Serve a responsive circuit lesson from static files and embed it in an iframe. Keep circuit edits and calculation within the lesson session. | Open the published app in an iframe, operate it with pointer and keyboard, and verify host-root and subpath asset loading. |
| SCH-MODEL-001 | Store each part's identity, ordered terminals and electrical values independently of drawing geometry. Wires join named endpoints. | Move geometry without changing nets. Coincident pins and crossing strokes do not connect by position. |
| SCH-EDIT-001 | Place a part, connect terminals, edit a value, delete the selected part with its incident wires, and reset the lesson. | Exercise each action. Refused values and invalid wires leave the document intact. |
| SCH-LESSON-001 | Evaluate the unloaded divider analytically. Report incomplete or unsupported topology without displaying a computed voltage. | Check equal resistors, a 3 kΩ lower resistor, a short and an open circuit. |
| SCH-PLOT-001 | Label the voltage probe and plot 21 DC sweep samples with axes in volts. On an invalid run, give feedback without presenting a trace as current. | Verify 3.75 V at 5 V after editing R2, then confirm a stale trace keeps its original samples and a refused run displays no current trace. |
| SCH-CAT-001 | Group and search generic symbols. Give each placed part a unique ID, named ordered terminals and explicit model readiness. | Search for a MOSFET, place it, inspect all four pins, and verify the divider refuses that topology. |
| SCH-NET-001 | Route wires with bends, add explicit junctions, name nets and highlight connected terminals. | Demonstrate crossing wires before and after adding a junction, repeated net labels, and correct highlight scope. |
| SCH-TXN-001 | Move, rotate, mirror and duplicate parts. Support undo and redo through commands. Cancelled gestures preserve the committed document. | Compare graph and geometry across edit, undo, redo and cancellation. |
| SCH-CHECK-001 | Show stable, actionable diagnostics for missing ground, dangling pins, incompatible models and unsupported analyses. Select a finding to highlight its object. | Attempt a run with one dangling resistor, confirm the blocking diagnostic and navigate to each affected pin. |
| SCH-PROBE-001 | Select a net to add its voltage trace in one action. Identify device terminal and sign for current probes. Use matching names and units on the sheet and plot. | Probe an RC output, probe a branch current, and show distinct feedback for unavailable data and hidden traces. |
| SCH-RUN-001 | Capture the circuit, plan, model identity and revision for each run. Retain completed, failed, cancelled and warning states and mark older results stale after an edit. | Run, edit and rerun a circuit. Inspect both snapshots and exercise cancellation and warning states. |
| SCH-RESULT-001 | Show multiple labeled voltage and current traces for supported DC, AC and transient analyses. Support cursor values and sample export. | Compare an RC cutoff and charging curve against analytical references. Check units and exported values for two signals. |
| SCH-LIB-002 | Separate symbol artwork, generic model families, model variants and user-provided subcircuits. Validate terminal order and analysis support. | Accept engineering notation, reject mismatched pins and missing models, and report DC-only capability before a transient run. |
| SCH-DOC-001 | Round-trip circuit, view, analysis and probe state in a versioned lesson document. Built-in examples open in memory. | Verify reload and export, version refusal and recovery, and opening a built-in example from an unsaved lesson. |
| SCH-CANVAS-001 | Support pan, zoom, fit and configurable snap. Make essential actions accessible from the keyboard and discoverable in narrow embeds. | Exercise keyboard placement, wiring and probing at desktop and narrow widths. Verify navigation preserves the graph. |
| SCH-HIER-001 | Define reusable cells with validated ordered ports and explicit multi-sheet net scope. Distinguish editing a definition from editing an instance. | Instantiate an amplifier twice and verify local nets, shared ports and mismatched port diagnostics. |
| SCH-CMD-001 | Route every user action through a stable typed command. Use one handler for equivalent keyboard and pointer activation. Describe availability and undo behavior. | Exercise commands via both input methods and check that neither adapter can mutate the board directly. |
| SCH-CMD-002 | Return structured success, refusal, cancellation or required input. Check document identity and revision before committed edits. | Attempt stale, invalid, cancelled and conflicting concurrent edits and check that no refused edit changes the document. |
| SCH-MCP-001 | Publish only selected typed commands as discoverable MCP tools. Return structured circuit, catalog, edit, run and result data. | Use a real client to discover tools, change R2 to 3 kΩ, and retrieve 21 samples ending at 3.75 V for a 5 V input. |
| SCH-MCP-002 | Scope commands to the host's workspace and document. Bound work and isolate sessions. Require explicit binding to share a browser session. | Start two hosts, verify distinct document IDs, and reject a command carrying the other host's document ID. |

The initial command contract uses zero-based terminal indices and explicit document revisions. Every accepted edit increments the revision once. Inspection, view changes, rejected edits and analysis runs leave the circuit revision unchanged. A stale draft can be recovered by reselecting the part and applying a fresh value. The current host retains the last 16 successful analytical sweeps per session. Undo and redo remain tracked by SCH-TXN-001.

The prototype supports the analytical divider and a generic symbol catalog. Routed wiring, model-backed analyses, cross-probing, general document persistence and hierarchy remain open. The browser and local stdio MCP host each own an independent session.

The detailed catalog adds 214 individually identified targets.

| Catalog | Targets | Scope |
| --- | ---: | --- |
| [Sources](source-catalog.sdoc) | 33 | Separate voltage and current waveform forms, controlled sources and switches |
| [Devices](device-catalog.sdoc) | 52 | Passives, transistor pin variants, amplifiers, protection, magnetics, transmission and sheet objects |
| [Digital and timing](digital-catalog.sdoc) | 42 | Logic, storage, timing, conversion and programmable blocks |
| [Analog and behavioral](behavioral-catalog.sdoc) | 44 | Analog blocks, interpolation, waveform generation, measurement and distributed elements |
| [Compact models](compact-model-catalog.sdoc) | 43 | Individually identified model bindings and per-analysis qualification |

Catalog targets describe scope and verification. The executable catalog currently has 18 generic symbols, and the analytical divider uses a bounded subset. Vendor-specific packs require their own manifests with exact identity, provenance, pin mapping and verified capabilities.
