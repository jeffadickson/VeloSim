# Real Surelog/UHDM execution gate

The native normalizer traverses one elaborated counter top through UHDM VPI and rejects unsupported constructs. This is a restricted counter frontend, not general SystemVerilog support.

Build pinned Surelog/UHDM with Ninja, install their CMake package, then configure this repository with `cmake -S . -B artifacts/with-uhdm -G Ninja -DVELOSIM_BUILD_UHDM=ON -DCMAKE_PREFIX_PATH=/opt/velosim` and `cmake --build artifacts/with-uhdm --parallel 4`.

Run `make test JOBS=4`, `make coverage JOBS=4`, and `make publish JOBS=4` under WSL. The compiled source-driven comparison is:

```sh
artifacts/publish/VeloSim.Tools/VeloSim.Tools differential --top slang_counter --output-dir artifacts/real-counter --cycles 1000 --threads 4 --build-jobs 4 --engines velosim,icarus,verilator examples/slang_counter.sv
artifacts/publish/VeloSim.Tools/VeloSim.Tools replay artifacts/real-counter/report.json --output-dir artifacts/replayed-counter
```

The native engine executes IR derived from this actual RTL through Surelog/UHDM. Icarus and Verilator independently compile the same RTL. Verilator uses CMake/Ninja and checks binary traces; four-state scheduler conformance is checked separately against an independent C# oracle. Replay verifies recorded artifact/tool fingerprints and semantic report consistency.

Verified WSL Ubuntu24.04, GCC13.3, Surelog `6db44ea2048c8ab9c702e389c2897f7e2318ccc5`, matching UHDM `7c42de4057d0a7e2ef14048b8ee570c7ac3db94b`. The normalizer resolves work-qualified tops and parameter widths through UHDM type evaluation. Historical Python experiments remain reference sources; active helpers and CLI are compiled C#.
