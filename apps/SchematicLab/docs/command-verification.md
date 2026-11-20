# Command layer verification

The browser and the local MCP host both call LessonSession.Execute. The browser uses one session per lesson. The stdio host uses one in-memory session per process. The agent identifies a document and revision before editing. The session serializes commands, refuses stale revisions and retains up to 16 successful sweeps. The browser labels the displayed trace stale when the circuit revision changes.

| Check | Result |
| --- | --- |
| Shared command unit tests | 11 tests passed, including stale edits, invalid and cancelled commands, and competing writes to one revision. The existing 256-case divider property used seed 1108. |
| Browser | Published Release output exercised in Chromium inside an iframe. Keyboard selection and pointer edits worked. The test recorded 3.75 V at 5 V, made a wire edit while a draft was open, and verified that applying the stale draft left R2 at 3 kΩ and retained the previous trace. |
| MCP | The official C# client started a stdio host, discovered nine tools, and read the retained 21-point sweep ending at 3.75 V for a 5 V input. Stale and invalid edits, malformed inputs and cross-session document IDs were refused. A second host had a distinct document and an unchanged 2.5 V output. |
| Mutation | An isolated change admitted older revisions. Both stale-edit and competing-edit tests detected it. |
| Requirements | StrictDoc 0.30.4 parsed eight documents with 234 unique IDs. |
| Coverage | Coverlet measured 185/264 lines and 85/168 branches in the C# core. Browser event handlers and the MCP transport are outside that instrumentation scope. |

The host exposes catalog, circuit, value edit, placement, wiring, deletion, reset, sweep and retained result operations. Tool calls have explicit outcomes. The circuit edits have no undo handler yet. The result store is in memory. The browser and MCP tests exercise the same handlers through independent sessions. An explicit binding to an already open browser session and a durable result store remain future work.

Raw logs and the browser screenshot are under E:/Artifacts/SchematicLab-commands-0890bac0.
