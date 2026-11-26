# Direct SystemVerilog Frontend via `MikePopoloski/slang` (No Surelog / No UHDM / No Python)

VeloSim embeds **`MikePopoloski/slang`** (MIT license, IEEE 1800-2023 compliant) as its direct SystemVerilog frontend and **bypasses UHDM completely**. Because VeloSim owns its canonical, validated [`velosim.rtl.v1`](../src/VeloSim.Core/RtlDesign.cs) intermediate representation, lowering straight from `slang`'s elaborated, parameter-resolved, type-checked AST to `velosim.rtl.v1` removes Surelog, UHDM, and Python from the simulation build path:

```text
SystemVerilog (.sv) ──▶ slang (MIT, IEEE 1800-2023) ──▶ velosim.rtl.v1 IR ──▶ C++20 / Rust / C# / 4-8x SIMD
```

## Architecture & Why We Do NOT Write Our Own Slang or UHDM

1. **Why embed `MikePopoloski/slang` instead of writing a custom parser/elaborator?**
   - Full IEEE 1800-2023 SystemVerilog (preprocessor, packages, generate loops, parameterized interfaces, signed/unsigned expression width propagation, constant folding) is a multi-year compiler effort (~250k LOC in `slang`).
   - `slang` is MIT-licensed, actively maintained, compiles cleanly as a C++20 library or standalone CLI (`slang --ast-json`), and resolves parameters and expression types ahead of lowering.
2. **Why drop UHDM?**
   - UHDM exists as a heavyweight C++ interchange graph between third-party tools. VeloSim already has a compact, strictly validated JSON IR (`velosim.rtl.v1`) tailored to compiled C++20, Rust (`#![forbid(unsafe_code)]`), C# 15 (zero `unsafe`), and lockstep SIMD emission. Going directly from `slang` AST → `velosim.rtl.v1` eliminates UHDM's build complexity, schema churn, and serialization overhead.

## Usage

### CLI (`velosim slang-lower`)

```bash
# Lower SystemVerilog directly via installed slang binary (or --slang /path/to/slang)
VeloSim.Tools slang-lower examples/rv32i_core.sv --top rv32i_core --output rv32i_core.rtl.json

# Or lower a pre-generated slang --ast-json file directly without invoking slang
VeloSim.Tools slang-lower --ast-json rv32i.ast.json --top rv32i_core --output rv32i_core.rtl.json

# Export directly to C++20, Rust, C#, or 8-lane SIMD
VeloSim.Tools rtl-export rv32i_core.rtl.json --top rv32i_core --output-dir out/rust_8l --target rust --two-state --lanes 8

# Lint SystemVerilog (slang --lint-only / -Weverything + VeloSim IR checks)
VeloSim.Tools lint examples/rv32i_core.sv --top rv32i_core -Wall -Werror

# Lint pure velosim.rtl.v1 IR or pre-generated slang --ast-json (zero external binaries)
VeloSim.Tools lint rv32i_core.rtl.json -Wall --json
VeloSim.Tools --lint-only --ast-json rv32i.ast.json --top rv32i_core --profile signoff --sarif
```

See [the flagship linter guide](linting.md) for rule profiles, `velosim-lint.json` configuration, scoped waivers, baseline ratcheting, SARIF 2.1.0 output, and `IRtlLintRule` extensibility.

### Optional In-Process C++20 Bridge (`native/slang`)

When `slang` development headers/CMake config are installed (`find_package(slang CONFIG REQUIRED)`), build the in-process C++20 lowerer with:

```bash
cmake -S . -B build -DVELOSIM_SLANG_FRONTEND=ON
cmake --build build --target velosim-slang-lower
```

## Real RV32I RISC-V Core Benchmark (`examples/rv32i_core.sv`)

`examples/rv32i_core.sv` is a complete single-cycle RV32I processor (`LUI`, `AUIPC`, `JAL`, `JALR`, all 6 branches, `LW`/`SW` + MMIO `0xFFFF_FFF0` `host_arg`, all `OP-IMM` and `OP` ALU/shift instructions, `EBREAK`/`ECALL` halt). It passes **0 errors, 0 warnings** in both `slang v12.2` and `verilator 5.055 --lint-only -Wall`.

Running a 50,000-iteration Fibonacci + DMEM store/load + hash-mix RV32I machine-code program (**700,009 cycles**, verified bit-for-bit `a0 = 0x2e0efb38`, `a1 = 50000` on every engine):

| Engine | Throughput (Mcyc/s) | Speedup vs Verilator 5.055 | Verification |
|---|---:|---:|---|
| **Verilator 5.055 (`-O3`, 2-state)** | 8.01 Mcyc/s | 1.00× | Reference (`0x2e0efb38`) |
| **VeloSim C++20 scalar (4-state)** | 3.39 Mcyc/s | 0.42× (full 4-state `X`/`Z`) | Bit-for-bit match |
| **VeloSim Rust scalar (4-state, `#![forbid(unsafe_code)]`)** | 3.93 Mcyc/s | 0.49× (full 4-state `X`/`Z`) | Bit-for-bit match |
| **VeloSim C# 15 scalar (4-state, 0 `unsafe`)** | 0.88 Mcyc/s | 0.11× (full 4-state `X`/`Z`) | Bit-for-bit match |
| **VeloSim C++20 scalar (2-state)** | **13.70 Mcyc/s** | **1.71×** | Bit-for-bit match |
| **VeloSim Rust scalar (2-state, `#![forbid(unsafe_code)]`)** | **16.88 Mcyc/s** | **2.11×** | Bit-for-bit match |
| **VeloSim C# 15 scalar (2-state, 0 `unsafe`)** | **9.08 Mcyc/s** | **1.13×** | Bit-for-bit match |
| **VeloSim C# 15 8-Lane SIMD (2-state, 8 RV32I cores/thread)** | **11.61 Mcyc/s** | **1.45×** | Lane 0 match + `diverged_mask=0xfe` |
| **VeloSim Rust 8-Lane SIMD (2-state, 8 RV32I cores/thread)** | **28.62 Mcyc/s** | **3.57×** | Lane 0 match + `diverged_mask=0xfe` |
