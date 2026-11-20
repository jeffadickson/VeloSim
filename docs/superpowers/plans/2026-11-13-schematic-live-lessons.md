# Live schematic lessons implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline. Keep builds
> serialized and all bulk outputs on E:. Obtain a fresh final code review.

**Goal:** Deliver a standalone schematic with its real solver, author-controlled
layout locking, and a documented path into live books and WALLY presentations.

**Architecture:** Keep the shared typed command/session layer authoritative for
interaction permissions. A trusted deployment supplies the author's lesson and
unlock policy; saved reader documents and MCP commands cannot relax that policy.
Browser rendering stays separate from solver execution. The package includes the
native solver and self-contained host rather than requiring a second install.

**Tech stack:** Existing .NET 11/C# 15 Core, Blazor WebAssembly, ASP.NET loopback
host and MCP host; existing Rust executable; existing Pester/FsCheck/xUnit tools.

**Spec:** New SCH-BOOK-* requirements in docs/requirements.sdoc. User chose that
the author decides whether readers may unlock the layout (2026-11-13).

## Reuse and deployment decisions

Reuse the existing native checkbox and typed command registry, not a new editor
or UI library. Reuse the existing solver bridge and publisher, validating actual
bundled execution. Drawing catalog breadth is not model readiness.

WALLY's local source/README identifies Quarto/reveal.js presentation output.
Use its existing trusted HTML embedding capability for an iframe first; no
WALLY runtime migration or source changes are authorized by this assessment.
Primary references: Quarto advanced reveal documentation and MDN postMessage.
MCP is an automation channel, not an embedding surface. An eventual agent bridge
must control the same visible document/session and preserve stale-edit checks.
The current separate browser and stdio MCP sessions must not be called shared.

Offline deployment serves slide HTML and the schematic from the same packaged
local host. An online book needs a hosted simulation service or the local
companion. Static HTML alone cannot execute a native executable. A future WASM
solver would be a separate qualified deployment, not an assumed capability.

## Increments

- [ ] Finish .NET 11 migration verification and package publish with solver.
  Validate hashes, launch outside the checkout, and verify real RC browser/MCP.
- [x] Add host-owned lesson interaction policy, typed lock/read commands and
  command-layer enforcement. Structural edits, routes, nets, hierarchy and
  document replacement refuse when locked. Parameter edits, probes and analysis
  remain available. Test stale edits and undo/redo bypasses without deleting
  history; preserve author policy across persistence.
- [x] Add a thin browser lock checkbox, hide/disable unlocking when the author
  forbids it, and expose the same supported operations in MCP. Verify real RC
  parameter changes and waveforms in both entry points.
- [x] Record a textbook-topic/model/analysis readiness matrix. Each ready lesson
  needs an actual circuit, analytical/ngspice evidence, probe/waveform tests and
  explicit limits. Track op-amp, oscillator, switching and advanced model gaps.
- [ ] Add a WALLY iframe authoring sample and embedding contract. Verify a real
  rendered example before claiming WALLY integration. Agent control of the same
  visible session remains open until implemented and exercised.
- [ ] Collect coverage and relevant negative/property/mutation checks, run real
  StrictDoc parsing, review, then commit verified increments and push VeloSim.

Layout locking is application behavior, not a security sandbox against a reader
who controls or modifies their own browser/runtime. Server policy remains the
authority for any hosted operations.

## Checkpoint ledger — November 14

The standalone Windows package with the real solver is published and has passed
browser iframe/MCP RC checks outside the source checkout. The complete .NET 11
solution builds with zero warnings/errors. Schematic tests: 72 passed; package
tests: 18 passed; StrictDoc: 13 documents parsed. Root Windows tests retain
15 failures and 141 explicit skips, so the broader migration verification box
remains open. See docs/schematic-book-verification.md and its bounded JSON.

Ruling: publish this verified RC/divider vertical slice on the development branch
without merging main or claiming whole-book/whole-VeloSim qualification. Root
Windows portability failures, additional model lessons, WALLY renderer execution
and shared visible-session agent control remain explicit follow-up work. The
iframe authoring sample is present; actual WALLY rendering remains unverified.

Review caught F/H rebind through the async analysis command. Its regression failed
before the guard and passed after it. A follow-up regression caught unrelated
malformed drafts incorrectly influencing the guard; ParseControls now provides
one independent parsing path. A fresh read-only reviewer rechecked the fixes.
