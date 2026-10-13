# VeloSim v0.17 — frontend-to-native orchestration

The pipeline coordinates a real Surelog executable, a separately built UHDM normalizer, strict normalized-IR lowering, and the C++ engine. It does not parse SystemVerilog text itself.

## Tests

```sh
python3 -m unittest discover -s tests -p test_pipeline.py -v
```

Four local tests passed. They use **mock Surelog and mock UHDM normalization** but the real native C++ kernel. The counter test checks 269 increments and rollover at 8 workers.

## Outstanding blocking task

The `velosim-uhdm-normalize` executable is **not implemented yet**. The current UHDM process AST inspector emits structural data, not the normalized schema. Thus running the real Surelog frontend through to native simulation is still blocked. Do not report this as real source-driven HDL execution.

The next implementation is a UHDM C++ normalizer using the pinned UHDM API, with explicit rejection of unsupported constructs, followed by actual Surelog smoke and differential tests.
