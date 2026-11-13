# Structural compilation handoff

Goal: address https://github.com/jeffadickson/VeloFormal/issues/2 without changing legacy compile output.

Design: add `compile-rtl` / `DesignPipeline.CompileRtlAsync` using an explicitly selected structural exporter. Retain exporter bytes in design.rtl.json after bounded JSON envelope validation; do not invoke either legacy lowering or the narrower simulation parser. Record requested top, ordered source paths and SHA-256, tool commands and artifact SHA-256 in compile-rtl.json. This validates the producer envelope, not simulator/formal semantic support.

1. Add bit-register source and pinned direct-export fixture from VeloFormal, CLI positive/negative regressions, determinism/source-provenance assertions, legacy routing diagnostic and opt-in actual-tool integration.
2. Run regressions before implementation. Add separate pipeline and CLI dispatch; reject unknown schema, duplicate fields, malformed envelopes, missing/oversized outputs and exporter failure. Publish no final IR or provenance on rejection; require an empty output directory.
3. Run targeted and full tests; build the actual native/toolchain path if available. Record limitations and reproduction commands, then commit on fix/structural-compile-handoff. Do not merge.
