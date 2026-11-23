# Multi-Target Feature & Example Parity Matrix (C++, Rust, C#)

This document is the canonical cross-target tracking ledger for VeloSim's three exported simulation backends: **C++20**, **Rust 2021 (`#![forbid(unsafe_code)]`)**, and **C# 15 / .NET 11 (zero `unsafe` blocks)**. Every RTL feature and every `examples/*.sv` design is tracked across all three targets and linked to StrictDoc requirements in [`requirements/velosim.sdoc`](../requirements/velosim.sdoc).

> **Policy (`VS-RTL-PARITY-001`):** Every new RTL or runtime capability must record its status across **C++**, **Rust**, and **C#** in this matrix. Features not yet implemented on Rust or C# must fail closed at export time via [`CanUsePortable64`](../src/VeloSim.Core/RtlCSharpEmitter.cs) or [`RtlExternalSdk.ExportAsync`](../src/VeloSim.Core/RtlExternalSdk.cs).

---

## 1. Target Runtime Summary

| Dimension | C++ (`--target cpp`) | Rust (`--target rust`) | C# (`--target csharp`) |
|---|---|---|---|
| **Language / Toolchain** | C++20 (`g++`/`clang++`, CMake/Ninja) | Rust 2021 (`cargo`, zero external crates) | C# 15 / .NET 11 (`dotnet`, zero NuGet deps) |
| **Core Safety Guarantee** | Native C++20 (`vsrtl::Model` + `vpi.cpp`) | `#![forbid(unsafe_code)]` on core model crate | Zero `unsafe` blocks; zero warnings (`-warnaserror`) |
| **Narrow 4-State Storage (`<= 64`-bit)** | 16-byte `vsrtl::w4::W4(a, b)` inline arrays | 16-byte `W4(a, b)` fixed inline arrays | 16-byte `W4(a, b)` + `[InlineArray(N)]` (`<= 4096`) |
| **2-State Storage (`--two-state`)** | `uint64_t` (`vsrtl::w2`) | `u64` (`w2`) | `ulong` (`W2Ops`) |
| **Wide Storage (`> 64`-bit)** | 256-bit default / 512-bit (`--large-design`) `vsrtl::W` | Gap (`VS-RTL-PARITY-005`) | Gap (`VS-RTL-PARITY-005`) |
| **Formal 4-State Proof** | `formal/dafny/FourStateW4Equivalence.dfy` (96/96 obligations) | `formal/dafny/FourStateW4Equivalence.dfy` | `formal/dafny/FourStateW4Equivalence.dfy` |
| **Emitter Files** | `RtlNarrow64.cs`, `RtlTwoState64.cs`, `RtlExternal.cs`, `native/rtl/` | `RtlRustEmitter.cs`, `RtlRustVpiEmitter.cs` | `RtlCSharpEmitter.cs`, `RtlCSharpVpiEmitter.cs` |

---

## 2. Feature Parity Matrix

Status legend:
- **Supported**: Implemented, tested, and verified on that target.
- **Gap (Tracked)**: Rejected fail-closed or unimplemented on that target; tracked by a StrictDoc requirement UID in [`requirements/velosim.sdoc`](../requirements/velosim.sdoc).

### 2.1 Core RTL Execution & Data Types

| Feature | C++ | Rust | C# | Requirement UID |
|---|---|---|---|---|
| 4-state `<= 64`-bit IEEE 1800 `aval`/`bval` (`W4`) | Supported | Supported | Supported | `VS-RTL-W4-PACKED-001` |
| 2-state `<= 64`-bit opt-in (`--two-state`) | Supported | Supported | Supported | `VS-RTL-W4-PACKED-001` |
| Combinational (clockless) acyclic settling | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Event-driven `posedge`/`negedge` & multi-clock processes | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Signed/unsigned arithmetic, shifts, reductions, mul/div/mod | Supported | Supported | Supported | `VS-RTL-W4-PACKED-001` |
| Bit-select, constant part-select, indexed `+:`/`-:` select, concat/repeat | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Selected & concatenated NBA targets (`<=`) | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| `case` / `casez` / `casex`, multi-label case, `==?`/`!=?`, `inside` | Supported | Supported | Supported | `VS-RTL-WILDCARD-001` |
| Multi-driver 4-state net resolution (`wire`/`wand`/`wor`/`tri0`/`tri1`) | Supported | Supported | Supported | `VS-RTL-RESOLVED-001` |
| Bit queries (`$countones`, `$onehot`, `$onehot0`, `$isunknown`, `$clog2`) | Supported | Supported | Supported | `VS-RTL-BITQUERY-001` |
| Wide packed words (`65..256`-bit default; `257..512`-bit `--large-design`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-005` |

### 2.2 Memories & SystemVerilog Containers

| Feature | C++ | Rust | C# | Requirement UID |
|---|---|---|---|---|
| Fixed 1D unpacked word memories (`<= 64`-bit, zero & non-zero lower bound) | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Fixed multidimensional arrays (rank 2–4) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-003` |
| Dynamic arrays (`m[]`: `new[n]`, `new[n](m)`, `delete()`, `size()`, `m[i]`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-003` |
| Bounded queues (`q[$]`: `push_front`/`back`, `pop_front`/`back`, `delete()`, `size()`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-003` |
| Associative arrays (traversal/lookup) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-003` |
| `$readmemh` / `$readmemb` file loaders (`loadMemoryHex`/`Bin`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-003` |

### 2.3 Control Flow, Startup & Explicit-Time Scheduling

| Feature | C++ | Rust | C# | Requirement UID |
|---|---|---|---|---|
| Static `repeat` and bounded runtime `while`/`do`/`repeat` loops | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| `initial` startup blocks | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-002` |
| Procedural blocking (`=`) statements inside `always` processes | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-002` |
| Discrete-event `#delay` timed scheduler (`hdl_scheduler.h`, `eventsPending`, `nextTimeSlot`, delayed NBA `<= #d`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-004` |

### 2.4 Verification, System Tasks & State Persistence

| Feature | C++ | Rust | C# | Requirement UID |
|---|---|---|---|---|
| `$display` / `$write` formatting, `$finish`, `$fatal`, RTL `assert` | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Plusargs (`$test$plusargs`, `$value$plusargs`) & seeded RNG (`$urandom`, `$urandom_range`, `$random`) | Supported | Supported | Supported | `VS-RTL-PLUSARGS-RNG-001` |
| Runtime `force` / `release` (procedural & host API) | Supported | Supported | Supported | `VS-RTL-FORCE-001` |
| Bounded concurrent SVA (`assert`/`assume`/`cover`, `disable iff`, `##N`, `$past`/`$rose`/`$fell`/`$stable`) | Supported | Supported | Supported | `VS-RTL-SVA-001` |
| Hierarchical 4-state VCD & RFC 1952 `.vcd.gz` with prefix filters | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Schema-hashed binary checkpoints (`saveCheckpoint` / `restoreCheckpoint`) | Supported | Supported | Supported | `VS-RTL-PARITY-001` |
| Functional `covergroup`s, bins, 2-way crosses, merge | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-005` |
| DUT branch/case/toggle coverage (`--coverage`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-005` |

### 2.5 Interop, VPI & Parallelism

| Feature | C++ | Rust | C# | Requirement UID |
|---|---|---|---|---|
| Native Safe VPI Session API (introspection, force/release, memory R/W, all 6 callback reasons) | Supported (`vpi.cpp`) | Supported (`VpiSession`) | Supported (`VpiSession<T>`) | `VS-RTL-VPI-MULTI-001` |
| IEEE 1800 C-ABI VPI (`vpi_user.h` + `vlog_startup_routines` plugins via `--vpi`) | Supported (built-in) | Supported (`vpi_ffi/` opt-in) | Supported (`vpi_ffi/` opt-in) | `VS-RTL-VPI-MULTI-001` |
| IEEE 1800 Annex I DPI-C (`svdpi.h` imports) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-005` |
| Multi-threaded combinational settle (`--threads > 1`) | Supported | Gap (Tracked) | Gap (Tracked) | `VS-RTL-PARITY-005` |
| Lockstep N-lane SIMD multi-seed & fault-injection (`--lanes 4\|8`, `DivergedMask_*`) | Supported (`alignas(32/64)`) | Supported (`#![forbid(unsafe_code)]`) | Supported (`Vector256`/`Vector512`, 0 `unsafe`) | `VS-RTL-SIMD-LANES-001` |

---

## 3. Complete `examples/*.sv` Corpus Parity (13 SystemVerilog Samples)

| # | Example File | Top Module | C++ (4s / 2s) | Rust (4s / 2s) | C# (4s / 2s) | Verilator 5.055 Checksum | Gap / Blocker |
|---|---|---|---|---|---|---|---|
| 1 | `examples/counter16.sv` | `counter16` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 2 | `examples/counter64.sv` | `counter64` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 3 | `examples/external_counter.sv` | `external_counter` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 4 | `examples/slang_counter.sv` | `slang_counter` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 5 | `examples/multiclock.sv` | `multiclock` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 6 | `examples/surelog_bitwise.sv` | `surelog_bitwise` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 7 | `examples/surelog_selections.sv` | `surelog_selections` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 8 | `examples/surelog_fifo.sv` | `surelog_fifo` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 9 | `examples/surelog_fifo_operators.sv` | `surelog_fifo_ops` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 10 | `examples/surelog_fifo_selections.sv` | `surelog_fifo_selections` | Supported | Supported | Supported | Match (all 7 engines) | — |
| 11 | `examples/dynamic_array.sv` | `dut` | Supported | Gap (`VS-RTL-PARITY-002`, `003`) | Gap (`VS-RTL-PARITY-002`, `003`) | C++ qualified | Dynamic array `m[]`, `initial`, blocking `=` |
| 12 | `examples/queue_demo.sv` | `queue_demo` | Supported | Gap (`VS-RTL-PARITY-002`, `003`) | Gap (`VS-RTL-PARITY-002`, `003`) | C++ qualified | Queue `pending[$]`, blocking `=` |
| 13 | `examples/timed_counter.sv` | `timed_counter` | Supported | Gap (`VS-RTL-PARITY-002`, `004`) | Gap (`VS-RTL-PARITY-002`, `004`) | C++ qualified | Explicit `#delay` time wheel, `initial`, `always #2`, `<= #1` |

**Current `examples/*.sv` coverage:** **13/13** C++, **10/13** Rust, **10/13** C#.

---

## 4. Measured Multi-Target Throughput Baseline

Median throughput (Mcyc/s, 2,000,000 cycles × 5 runs, 100% bit-for-bit checksum match against Verilator 5.055 `-O3`) recorded after PR #52 (16-byte `W4` in C++ and Rust) and PR #53 (C# `[InlineArray]`, stack-local NBA, pure-internal `Settle()` skipping, branchless `W4Ops`, and adaptive `EvalEdge()` outlining):

| Sample | Verilator 5.055 (`-O3`, 2s) | C++ 4s (`-flto`) | C++ 2s (`-flto`) | Rust 4s (`--release`) | Rust 2s (`--release`) | C# 4s (.NET 11) | C# 2s (.NET 11) |
|---|---:|---:|---:|---:|---:|---:|---:|
| `counter16.sv` | 15.92 | 76.20 | 570.12 | 651.81 | 847.46 | 392.27 | 516.96 |
| `surelog_selections.sv` | 14.84 | 45.84 | 312.50 | 274.24 | 367.15 | 152.68 | 213.04 |
| `surelog_fifo.sv` | 13.38 | 38.36 | 115.21 | 65.36 | 131.75 | 75.15 | 120.97 |
| `surelog_fifo_operators.sv` | 12.89 | 37.37 | 111.73 | 69.33 | 138.01 | 74.43 | 112.13 |
| `surelog_fifo_selections.sv` | 13.16 | 37.63 | 113.90 | 73.71 | 133.72 | 60.15 | 116.98 |

### 4.1 Measured Lockstep N-Lane SIMD Throughput (`--lanes 4|8`, AVX-512 Xeon Platinum 8481C)

Effective per-core throughput (`cycles × lanes / median_seconds`, 2,000,000 cycles × 5 runs, 100% bit-for-bit identical output hash across all targets and state modes):

| Sample | Lanes | C++ 4s | C++ 2s | Rust 4s (`#![forbid(unsafe_code)]`) | Rust 2s (`#![forbid(unsafe_code)]`) | C# 4s (0 `unsafe`) | C# 2s (0 `unsafe`) |
|---|---:|---:|---:|---:|---:|---:|---:|
| `counter16.sv` | 4 | 175.2 Mcyc/s | 551.2 Mcyc/s | 9,270.0 Mcyc/s | 9,697.0 Mcyc/s | 425.0 Mcyc/s | 1,073.7 Mcyc/s |
| `counter16.sv` | 8 | 213.4 Mcyc/s | 245.1 Mcyc/s | 14,678.9 Mcyc/s | 19,207.7 Mcyc/s | 820.7 Mcyc/s | 1,643.7 Mcyc/s |
| `surelog_fifo.sv` | 4 | 35.4 Mcyc/s | 173.1 Mcyc/s | 96.4 Mcyc/s | 260.3 Mcyc/s | 119.0 Mcyc/s | 218.0 Mcyc/s |
| `surelog_fifo.sv` | 8 | 58.0 Mcyc/s | 94.2 Mcyc/s | 108.4 Mcyc/s | 322.0 Mcyc/s | 134.8 Mcyc/s | 253.0 Mcyc/s |

---

## 5. Tracked Parity Roadmap (StrictDoc Work Packages)

1. **`VS-RTL-PARITY-002` — Startup `initial` & Procedural Blocking (`=`) in Rust and C#**
   - Unblocks sequential initialization blocks and mixed blocking/NBA `always` bodies while preserving `W4`/`u64` fast paths when unused.
2. **`VS-RTL-PARITY-003` — Dynamic Arrays, Queues, Associative & Multidimensional Arrays in Rust and C#**
   - Maps SV `m[]` → `Vec<W4>`/`List<W4>`, `q[$]` → `VecDeque<W4>`/`LinkedList<W4>` (or ring buffer), associative arrays → `BTreeMap`/`SortedDictionary`, and `$readmemh`/`$readmemb` loaders — closing `examples/dynamic_array.sv` and `examples/queue_demo.sv` to reach **12/13** examples across all three targets.
3. **`VS-RTL-PARITY-004` — Explicit-Time `#delay` Scheduler in Rust and C#**
   - Ports the bounded active/inactive/NBA discrete-event time wheel (`eventsPending`, `nextTimeSlot`, delayed NBA `<= #d`, resumable `#delay` / signal waits) to safe Rust and safe C# — closing `examples/timed_counter.sv` to reach **13/13** examples across all three targets.
4. **`VS-RTL-PARITY-005` — Wide Words (`> 64`-bit), DPI-C, Covergroups, DUT Coverage & Parallel Settle in Rust and C#**
   - Closes the remaining non-example parity gaps against the full C++20 runtime (`256`/`512`-bit words, `svdpi.h` FFI imports, `covergroup`s, `--coverage`, and `--threads > 1`).
