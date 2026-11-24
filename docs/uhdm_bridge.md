# UHDM Integration (.NET 11 C# Port & Native C++ Reference)

VeloSim provides a complete, zero-dependency, NativeAOT-clean **.NET 11 / C# 15 UHDM layer** under `src/VeloSim.Core/Uhdm/` alongside the original `native/uhdm/` C++ reference binaries.

## Pure .NET 11 / C# 15 UHDM Stack (`src/VeloSim.Core/Uhdm/`)

- **`CapnpWire.cs`** — Pure safe .NET 11 Cap'n Proto packed stream codec (`CapnpPackedStream`), multi-segment message reader with far-pointer resolution and strict traversal/nesting budgets (`CapnpMessageReader`), and single-segment message builder (`CapnpMessageWriter`).
- **`UhdmVpi.cs` & `UhdmSchema.cs`** — Exact VPI/SV-VPI/UHDM constants and Cap'n Proto `HoleSet`-packed struct metadata for all 231 UHDM classes (208 concrete factories) matching pinned `chipsalliance/UHDM` commit `7c42de4057d0a7e2ef14048b8ee570c7ac3db94b`.
- **`UhdmGraph.cs`** — In-memory UHDM object graph (`UhdmDatabase`, `UhdmNode`) supporting VPI-style traversal (`One`, `Many`, `GetInt`, `GetString`), direct field access, and binary Cap'n Proto `.uhdm` load/save.
- **`UhdmInspector.cs`** — 1:1 port of `native/uhdm/inspect.cpp` emitting `velosim.uhdm.inventory.v1`.
- **`UhdmNormalizer.cs`** — 1:1 port of `native/uhdm/normalize.cpp` emitting `velosim.uhdm.normalized.v1`.
- **`UhdmRtlAudit.cs`** — 1:1 port of `native/uhdm/rtl_audit.h` performing bounded cycle-safe traversal of all UHDM relations and failing closed on unsupported executable constructs.
- **`UhdmRtlLowerer*.cs`** — 1:1 thread-safe port of `native/uhdm/rtl.cpp` and all helper headers (`rtl_constants.h`, `rtl_original_expr.h`, `rtl_types.h`, `rtl_subroutines.h`, `rtl_flatten.h`) emitting `velosim.rtl.v1`.

## CLI Commands

```sh
VeloSim.Tools uhdm-inspect <surelog.uhdm> [--output inventory.json]
VeloSim.Tools uhdm-normalize <surelog.uhdm> --top <module> --output normalized.json
VeloSim.Tools uhdm-rtl <surelog.uhdm> --top <module> --output design.rtl.json [--large-design]
```

Existing workflows (`compile`, `compile-rtl`, `differential`, `fifo-differential`) accept `--normalizer builtin` / `--exporter builtin` (or `VELOSIM_UHDM_NORMALIZER=builtin` / `VELOSIM_UHDM_RTL=builtin`) to run the pure .NET 11 UHDM layer in-process, and automatically fall back to it when native `velosim-uhdm-*` executables are not present on `PATH`.

## Differential Parity Verification

`.github/workflows/differential.yml` builds pinned Surelog + `native/uhdm/` and diffs the outputs of C++ `velosim-uhdm-inspect`, `velosim-uhdm-normalize`, and `velosim-uhdm-rtl` against pure .NET 11 `VeloSim.Tools uhdm-inspect`, `uhdm-normalize`, and `uhdm-rtl` on real Surelog binary `.uhdm` files.
