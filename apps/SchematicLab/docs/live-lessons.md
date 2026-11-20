# Live lessons and presentation embedding

The standard standalone publication requires a trusted native solver executable.
`-EditorOnly` explicitly produces a drawing/analytical-divider distribution;
it is not an in-place native simulation package. The manifest records package
kind, every file hash and the caller-declared native source revision. A source
declaration is not a reproducible-build attestation. Optional BSIM providers are
not automatically included or qualified by copying the main executable.

## Author interaction policy

Configure the host and MCP process with the same deployment values:

* `SCHEMATIC_LAYOUT_MODE=author-locked`: layout fixed, reader cannot unlock.
* `SCHEMATIC_LAYOUT_MODE=reader-lock`: initially locked, reader may toggle it.
* `SCHEMATIC_LAYOUT_MODE=editor` (default): normal editable layout.
* `SCHEMATIC_LESSON=rc` or `divider` (default): initial built-in lesson.

Unknown values refuse startup. The browser obtains its profile from its own
host's `/api/lesson-deployment`; missing bootstrap data does not fall back to an
editable lesson. Structural commands refuse in the Core session. Values,
inspection and analyses remain available. Wiring-control references are
structural, even though they appear among analysis fields. Undo/redo can traverse
parameter edits while locked, but cannot cross a structural edit. Lock changes
advance the revision, cancel gestures and retain edit history. Save does not
grant permission; document replacement is refused while locked. Deployment
policy is not imported from a reader's saved sheet.

The UI checkbox and MCP `schematic_set_layout_locked` use the same typed handler.
`schematic_read_interaction_policy` reports the current mode and author permission.
This is an interaction contract, not a sandbox against a reader modifying their
own browser. A hosted numerical service still needs its own trust boundaries.

## Textbook readiness: bounded, not full-book qualification

Drawing a symbol does not establish an executable model. The following matrix
describes integration scope, not closure of every electronics teaching topic.
Existing numerical evidence lives in `bridge-verification.md`; new deployment
verification must additionally exercise the published package.

| Teaching topic | Current executable path | Important remaining limit |
|---|---|---|
| Divider and source loading | Analytical divider; general DC bridge | Only documented topology/model subsets |
| RC charging | Native transient and capacitor voltage probe | Fixed bounded transient settings; not arbitrary stiff switching circuits |
| RLC and filters | Native RLC DC/AC/transient bridge | Each lesson needs frequency/time reference checks |
| Independent and controlled sources | DC/phasors and bounded PULSE/PWL/sine/exponential profiles; E/G/F/H | File-backed and other catalog symbols may be drawing-only |
| Diode circuits | Bounded DC, AC linearization and transient charge | Not all vendor models, temperature or breakdown behavior |
| MOS circuits | MOS1 and its documented charge subset; optional BSIM3 provider | Geometry/defaults and high-bias convergence require individual evidence |
| BJT amplifiers | Bounded BJT bridge and storage profiles | Full Gummel–Poon and every newer parser-side parameter are not exposed automatically |
| SOI | Optional FD-SOI operating point | Dynamic charge and AC/transient remain unavailable in this bridge |
| Op-amp feedback | Symbols available | General op-amp-to-solver execution and rail/slew/GBW models remain open |
| Oscillators, power and switching | Some underlying elements execute | No general lesson-family qualification yet |
| Hierarchical lessons and reusable models | Bounded passive child sheets and named models | General hierarchical semiconductor workflows remain open |

Art of Electronics- or Sedra/Smith-style teaching is the target use case, not a
claim that all circuits in either book run today. Use original teaching fixtures,
explicit units/model assumptions and independent analytical/ngspice references.

## WALLY and web books

Use an iframe as the visual surface in WALLY's Quarto/reveal.js HTML output.
See `examples/wally-live-rc.qmd`. The existing browser smoke test also places the
lesson in an iframe; that alone is not qualification of the WALLY renderer.

For offline delivery, serve generated slides and the widget from the packaged
local host. For an online book, deploy a compatible numerical service or use a
local companion. A static HTML file cannot launch the native solver by itself.
PDF/PPTX exports need static snapshots and an explicit link to the live lesson.

MCP is the automation surface, not the display surface. Browser and stdio MCP
currently own independent sessions, even with the same deployment profile. They
must not be described as controlling the same visible document. A future bridge
needs session identity, expected revisions, correlated typed responses and exact
origin/source validation for browser messages. That shared-session bridge and
an actual WALLY-rendered integration remain open requirements.
