# RSpice schematic requirements scrub

## Live browser exercise

In the public RSpice WASM lab, I opened the component shelf, placed a resistor in an untitled sheet and opened its properties by double-clicking. The shelf displayed 152 built-in entries and separate groups for generated Verilog-A and library parts. The resistor inspector exposed resistance in engineering notation, multiplier, scale, AC resistance, model binding, temperature terms and noise. Running the lone resistor exposed a critical missing-ground finding, two unconnected-terminal warnings and a blocking run-preflight error.

I then opened the bundled RC Lowpass Filter. Its drawing showed a resistor, capacitor, AC source and a labeled output net. Opening the example triggered a browser file-operation dialog. Canceling that operation left the example on the sheet. Run completed one transient analysis with one warning. The Results data browser listed V(out) and V(net1) for the retained run. Selecting V(out) left the waveform pane at “No data” in this exercise, so trace rendering and cursor behavior remain unverified. I did not use the browser run as a numerical oracle. [RSpice live lab](https://rspice.app/ide/)

## Disposition

| Capability | Evidence | SchematicLab disposition |
| --- | --- | --- |
| Part shelf, placement and typed inspector | Live exercise | Search and 18 generic symbols are implemented. Draft SCH-LIB-002 covers model binding, engineering notation and analysis capability. |
| Wire routing, labeled nets, junctions and net highlight | Guide, with a labeled RC net seen in the live example | SCH-MODEL-001 preserves independent electrical endpoints. Draft SCH-NET-001 adds routing, explicit joins and net inspection. |
| Move, stretch, duplicate, rotate, mirror and viewport controls | Guide | Draft SCH-TXN-001 and SCH-CANVAS-001. |
| Rule checks and blocked run | Live incomplete circuit | Draft SCH-CHECK-001. The current lesson only hides its result for invalid topology. |
| Plans, run status and retained dataset | Live RC run | Draft SCH-RUN-001. The prototype currently computes its analytical divider sweep in browser memory. |
| Selecting a signal, plots and probes | Signal selection observed live. Additional plotting behavior documented. | Draft SCH-PROBE-001 and SCH-RESULT-001. Make the selected schematic net appear in the plot in one action and explain missing data in the pane. |
| Built-in examples and recovery | Live example with browser file-operation interruption | Draft SCH-DOC-001. Let lessons open as in-memory copies and separate explicit export. |
| Hierarchy, symbol editing, sheets and buses | Guide | Draft SCH-HIER-001. Defer until the single-sheet workflow is qualified. |
| Drawing exports, report documents and advanced plot types | Guide | Later evaluation. No new acceptance claim. |

[RSpice schematic guide](https://rspice.app/docs/schematic-entry), [simulation setup](https://rspice.app/docs/simulation-setup), [results guide](https://rspice.app/docs/results)

## First implementation sequence

1. Route and name nets, then add reversible edits and actionable preflight.
2. Connect a real solver-backed RC lesson with a visible voltage probe. Keep the analytical divider as a fast, independent reference.
3. Show trace and run provenance together, with an explanation when the selected signal has no data.
4. Add portable lesson documents and model binding. Expand hierarchy and the catalog after the same workflow is verified on a BJT amplifier and a CMOS inverter.
