# View and analysis history verification

Windows, .NET SDK 10.0.403, Chromium headless and the native RC solver used
for the previous wiring checkpoint. Artifacts are in
`E:/Artifacts/SchematicLab-session-3969ded`.

- `dotnet test Tests --collect "XPlat Code Coverage"` passed 23 tests with
  no skips. The suite exercises legacy sheet import, net membership across
  labels and junctions, malformed payloads, undo and redo, and the distinction
  between current and historical runs. The seeded truncated-document check
  from the wiring suite still runs with seed 110821.
- Coverlet instrumented Core including generated serializer code. It
  recorded 3,211 of 3,663 lines and 677 of 1,072 branches. Browser, MCP and
  native solver coverage were not measured.
- The isolated case-sensitive label mutant failed the whole-net assertion.
  Two other focused tests passed. A Stryker campaign was outside this run.
- The embedded browser smoke checked three highlighted terminals, two labels,
  one wire, the selected probe and catalog filter after reloading the page
  and sheet. All 502 plotted points matched the pre-save trace. The screenshot
  was inspected.
- The stdio MCP smoke imported the same session format into a second host,
  read the exact samples and original run version, and checked net membership
  and freshness. The native waveform differed from the analytic response by
  at most 41.253 microvolts.
- StrictDoc parsed 11 documents and found 244 unique requirement IDs.
