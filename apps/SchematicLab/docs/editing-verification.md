# Editing and preflight verification

The browser and stdio MCP host share typed move, rotate, mirror, duplicate, undo, redo and circuit check operations. The browser uses selectable grid destinations for move and duplicate. The MCP host accepts explicit coordinates. Terminal anchors rotate and reflect with the symbol while wire endpoints retain their electrical IDs.

History retains 32 prior board states per session, including the allocator. Undo and redo advance the revision and clear the active selection and gesture. New edits discard redo history. Refused edits leave history intact. Geometry changes preserve the divider reading.

The circuit check reports missing ground, dangling terminals and unsupported topology for the divider and RC lessons. Browser findings can highlight the affected terminal. Refused runs return any applicable circuit findings.

- Seventeen xUnit tests passed. The isolated mutation that preserved redo history after a new edit was caught. The existing divider property still uses 256 cases with seed 1108.
- The Chromium iframe verified keyboard move, rotation, undo/redo, unchanged divider voltage and terminal highlighting.
- The MCP client verified move, undo/redo, stale redo refusal and structured findings. It also reran the native RC regression with 502 samples and a maximum analytical discrepancy of 41.3 µV.
- Coverlet measured 400/490 lines and 213/344 branches in the C# core. Browser and MCP adapters and the native solver are outside that collector's scope.
- StrictDoc parsed 10 documents with 239 unique IDs. SCH-EDIT-GEOMETRY-001, SCH-EDIT-HISTORY-001 and SCH-CHECK-BASIC-001 trace this slice.

Automatic wire drawing still uses the existing simple routing rule, which can cross artwork or other wires after a move. Named nets, editable bends and junctions, persistent documents, hierarchy, model-specific checking and additional solver profiles remain open.

Logs and the screenshot are under E:/Artifacts/SchematicLab-edits-064cfde7.
