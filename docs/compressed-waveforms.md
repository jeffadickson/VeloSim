# Compressed digital waveform output

Requirement **VS-TRACE-COMPRESSED-001** is Draft in [StrictDoc](../requirements/velosim.sdoc), owned by [WP-17 / GitHub issue #18](https://github.com/jeffadickson/VeloSim/issues/18). The user requested compressed waveform output; implementation and verification remain open.

The product must provide losslessly compressed traces in a documented format that an independent reader can open. Planned acceptance checks preserve selected signal identities/hierarchy, declared widths, supported 0/1/X/Z values, timestamps and time precision. Errors and unsupported trace selections must be explicit. Existing `VS_RTL` comparison text alone does not meet the requirement. These detailed checks are inferred acceptance criteria pending implementation evidence.

## Implementation information: libfst

**FST is the initial format candidate.** [GTKWave libfst](https://github.com/gtkwave/libfst) is the library candidate recorded as [LIB-11](library-opportunities.md#lib-11). It supplies format machinery; VeloSim still supplies signal/hierarchy metadata, values and correctly scheduled time samples. See the [GTKWave FST documentation](https://gtkwave.github.io/gtkwave/internals/fst-file-format.html).

libfst is informational, not a mandatory dependency. Before adoption, pin a revision, review bundled compression/component notices, test the native build and exported-SDK packaging on the intended platforms, and document reader compatibility. No compression ratio, throughput gain, installed dependency or implemented waveform support is claimed by this requirement.

## Planned verification

- Decode a generated compressed trace using an independent reader and compare every expected value transition and timestamp with a small golden design.
- Include hierarchy, wide words, X/Z, unchanged values, trace selection, empty traces and clean shutdown.
- Test invalid paths, write/flush failures, interrupted output and supported Windows/Linux builds.
- Measure file size and write/read overhead on comparable traces; report the actual result without requiring an invented compression ratio.
- Record source/tool/library hashes and implementation coverage. Keep requirement status Draft until the accepted verification scope supports closure.
