# VeloSim v0.17 — frontend-to-native orchestration

The pipeline coordinates a real Surelog executable, a separately built UHDM normalizer, strict normalized-IR lowering, and the C++ engine. It does not parse SystemVerilog text itself.

## Tests

```sh
python3 -m unittest discover -s tests -p test_pipeline.py -v
```

Four local tests passed. They use **mock Surelog and mock UHDM normalization** but the real native C++ kernel. The counter test checks 269 increments and rollover at 8 workers.

## Real frontend integration

The `velosim-uhdm-normalize` executable now builds and runs against the pinned UHDM API. The real counter smoke test passes through Surelog, binary UHDM normalization, strict lowering, and the native engine. See [the real UHDM gate](real_uhdm_gate.md) for revisions, commands, and the verified scope.

The next step is to extend supported process/expression shapes with explicit rejection of unsupported constructs and connect source-driven compilation to the differential harness.
