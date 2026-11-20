# SchematicLab design

RSpice separates the electrical document and net extraction from editing and painting. SchematicLab follows that separation using an independently implemented C# model and original SVG paths. [RSpice design](https://github.com/JaimeHW/RSpice/tree/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-design), [RSpice editor](https://github.com/JaimeHW/RSpice/tree/4fa86cf4d97d474734d4692ca34912c0aad98f56/crates/rspice-schematic-editor)

The catalog distinguishes electrical kind, stable terminal indices, and instance value from symbol art and placement. The next catalog can give each part a stable definition ID, named terminal metadata and separately versioned SVG artwork. Pins should be explicit metadata so a graphical revision cannot change net identity. Browser-native SVG provides rendering and focusable targets now. An external SVG import requirement would need a constrained asset format and a separate validation pass before choosing an importer. A usvg port would only serve that later import path.

Standalone Blazor WebAssembly keeps the application in C# and deploys as static files. SVG gives the small lesson a browser-native drawing surface with focusable targets and responsive scaling. ImGui.NET and WebGPU bindings supply lower-level primitives and would require extra hosting and accessibility work for this lesson. The shared GUI catalog describes native Avalonia contributions, while this prototype targets an embeddable browser surface. [Blazor deployment](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/), [SVG symbol element](https://developer.mozilla.org/en-US/docs/Web/SVG/Reference/Element/symbol)

The graph traversal terminates because each queued pin is inserted into the visited set once. Reachability symmetry and artwork independence are candidates for a bounded Dafny trial when the graph contract grows. Dafny execution is outside this prototype's evidence. Tests check behavior through the same C# operations the UI uses.

## Traceability

| Requirement | Implementation | Verification |
| --- | --- | --- |
| SCH-WEB-001 | Web/Program.cs, Web/Lesson.razor, Web/wwwroot/index.html | Release publish and browser interaction |
| SCH-MODEL-001 | Core/Board.cs | CoordinatesAloneDoNotConnectParts |
| SCH-EDIT-001 | Core/Board.cs, Web/Lesson.razor | DeletingPartRemovesOnlyItsWires, InvalidEditsPreserveDocument |
| SCH-LESSON-001 | Core/Board.cs | DividerRespondsToValueChanges, SeededDividerValuesMatchIndependentEquation |

SCH-PLOT-001 is implemented in Board.Sweep and Lesson.razor. SweepTests and the embedded browser smoke test verify sweep endpoints and incomplete-circuit handling.

The SVG path definitions were authored for this prototype. They use conventional circuit symbol shapes. The source includes no third-party symbol assets.
