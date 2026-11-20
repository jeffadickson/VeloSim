# SchematicLab source and requirements audit

Baseline: 07ac172. This audit covers the browser application, board, catalog and tests. It uses prior runtime evidence recorded in verification.md and direct source inspection. The Rust Spice engine is a separate integration dependency and was outside this source audit.

| Existing requirement | Source and evidence | Disposition |
| --- | --- | --- |
| SCH-WEB-001 | Web/Program.cs, Web/wwwroot/index.html, BrowserSmoke.cjs | Verified for the tested Chromium iframe and root deployment. Other browsers and subpath hosting were not exercised. |
| SCH-MODEL-001 | Board.cs, PartCatalog.cs, LessonTests | Explicit terminal identity is implemented and coincidence is tested. The prototype has no editable routed segments or junctions. |
| SCH-EDIT-001 | Lesson.razor, Board.cs, LessonTests, BrowserSmoke | Placement, wiring, value edits, deletion and reset work in the tested slice. These are UI handlers, so command parity is open. |
| SCH-LESSON-001 and SCH-PLOT-001 | Board.Output, Board.Sweep, SweepTests, BrowserSmoke | Verified for the analytical divider. The run button enables a live redraw; editing immediately changes the trace. There is no immutable run, run ID or stale marker. |
| SCH-CAT-001 | PartCatalog, Lesson.razor, CatalogTests, BrowserSmoke | Verified generic catalog subset. Model binding and simulation capabilities beyond the divider remain open. |
| SCH-NET-001, SCH-TXN-001, SCH-CHECK-001, SCH-DOC-001, SCH-HIER-001 | Board and Lesson source | Open. Terminal connections and reset are a foundation. There is no routed document model, transactional history, structured preflight, persistence or hierarchy. |
| SCH-PROBE-001, SCH-RUN-001, SCH-RESULT-001 | Lesson and Board source | Open. A fixed voltage probe and analytical plot exist. Cross-probing, recorded runs, current traces, cursors and export are missing. |
| SCH-LIB-002, SCH-CANVAS-001 | Catalog and Lesson source | Partial foundation. Named pins, search and keyboard activation exist. Model binding, engineering input, and navigable canvas controls remain open. |

## User actions and command boundaries

| Present entry point | Proposed stable action | Agent exposure |
| --- | --- | --- |
| Quick palette and library choose; search and disclosure | Arm placement, set catalog filter, set panel visibility | Query the catalog directly, independent of filters. |
| Placement target click or key | Place part with kind and explicit coordinates | Yes. Return the new instance ID and revision. |
| Part selection, value input and Apply | Select part, edit draft value, set part value | Set value by explicit part ID and quantity. Selection and draft input are UI commands. |
| Terminal clicks or keys | Select first terminal, connect explicit pins, cancel gesture | Connect explicit pins in one call. Never depend on a pending click. |
| Delete and reset | Delete part; replace document with named example | Yes, with an explicit document and expected revision. Record undo policy. |
| Run sweep and render plot | Run analysis, then query result by run ID | Yes. Display toggling and rendering remain view operations. |
| Browser focus and activation mechanics | Translate DOM events to the registered action | Browser-generated focus notifications and key repeats are input mechanics. They must not execute a second application action. |

The highest priority gap is in Web/Lesson.razor. It owns document state, runs Board edits directly, and tracks selection, pending pins and tool mode in private fields. Expected Board failures are generally booleans, which lose the reason an agent needs to recover. The next slice should extract a host-independent session and typed dispatcher, then migrate all existing interactions, including draft input and cancel. Keep Board's graph traversal and SVG drawing helpers as internal calculations.

RSpice's registry provides useful precedents for stable IDs, aliases, owner, context and availability. For SchematicLab, add typed request and result contracts plus a tested mutation boundary. Use the official C# MCP SDK in a separate local stdio host. Discover and invoke an explicit tool allowlist through the same dispatcher. An initial agent session can own its own in-memory document. Connecting to an already open iframe requires a deliberate shared-session design because the current browser state lives in WebAssembly memory.

Acceptance sequence: use a real MCP client to discover tools, load a divider, inspect its revision, set R2 to 3000 ohms, run the 21-point sweep and read 3.75 V at 5 V. Run the same scenario from the browser and compare structured outcomes. Verify that stale, malformed, cancelled and missing-input operations do not partially edit the document. Add architecture checks that prohibit UI and MCP adapters from calling Board mutators directly.

References: [RSpice command registry](https://github.com/JaimeHW/RSpice/blob/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-app-workbench/src/workbench/framework/command_registry.rs), [MCP tools specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk).
