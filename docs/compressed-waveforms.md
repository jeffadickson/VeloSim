# Compressed digital waveform output

Requirement **VS-TRACE-COMPRESSED-001** remains Draft in [StrictDoc](../requirements/velosim.sdoc), owned by [WP-17 / GitHub issue #18](https://github.com/jeffadickson/VeloSim/issues/18), while its first verified lossless compressed waveform vertical slice is active under **VS-RTL-VCD-001**.

## Implemented lossless slice: RFC 1952 `.vcd.gz` (zero external dependencies)

- **C++20 SDK (`native/rtl/gzip_stream.h`, `native/rtl/vcd_trace.h`, `native/rtl/external_model.h`, `native/rtl/vpi.cpp`):** `Model::openVcdGz(path, timescale, filter_prefixes)` writes an RFC 1952 GZIP stream containing RFC 1951 LZ77 fixed-Huffman DEFLATE blocks and IEEE CRC-32 / ISIZE trailers with zero external library dependencies. Both `openVcd` and `openVcdGz` support fail-closed hierarchical `filter_prefixes` signal selection (unmatched selectors throw `std::invalid_argument`) and explicit open/write/flush/close error reporting.
- **C# (.NET 11) SDK (`src/VeloSim.Core/RtlCSharpEmitter.cs`):** Generated models provide `OpenVcdGz(path, timescale, filterPrefixes)` backed by `System.IO.Compression.GZipStream` with matching hierarchical `$scope` trees and fail-closed selector checks.
- **Safe Rust 2021 SDK (`src/VeloSim.Core/RtlRustEmitter.cs`):** Generated `#![forbid(unsafe_code)]` crates provide `open_vcd_gz`, `open_vcd_filtered`, and `open_vcd_gz_filtered` backed by a zero-dependency pure-Rust RFC 1952 GZIP + RFC 1951 LZ77 fixed-Huffman DEFLATE writer with RAII `Drop` trailer finalization and fail-closed `InvalidInput` rejection on empty/unmatched selectors.
- **Verification (`tests/VeloSim.Tests/RtlVerilatorGapsWave3Tests.cs`, `tests/VeloSim.Tests/RtlVerilatorGapsWave4Tests.cs`):** Independent decompression via .NET `GZipStream` verifies byte-for-byte equality with uncompressed VCD across C++ and safe Rust, strictly smaller file size on multi-cycle traces, preserved timescale/hierarchy/widths, and fail-closed rejection of unmatched trace selectors.

## Remaining optional follow-up: libfst and cross-platform qualification

**FST remains an optional binary-format follow-up.** [GTKWave libfst](https://github.com/gtkwave/libfst) is recorded as [LIB-11](library-opportunities.md#lib-11) for random-access binary waveforms (see the [GTKWave FST documentation](https://gtkwave.github.io/gtkwave/internals/fst-file-format.html)). libfst is informational, not a mandatory dependency. Before adoption, pin a revision, review bundled compression/component notices, test the native build and exported-SDK packaging on Windows and Linux, and document reader compatibility.

## Remaining verification before closing VS-TRACE-COMPRESSED-001

- Clean-machine Windows build/test qualification of `.vcd.gz` output and error-path handling (invalid paths, write/flush failures, interrupted output).
- Optional FST writer qualification if binary random-access waveform support is prioritized beyond standard GTKWave-readable `.vcd.gz`.
- Keep **VS-TRACE-COMPRESSED-001** Draft until cross-platform verification and final format scope support closure.
