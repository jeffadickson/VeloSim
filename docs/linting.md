# VeloSim Flagship Linter (`VeloSim.Tools lint`)

VeloSim includes a dual-layer, NativeAOT-compatible SystemVerilog and RTL IR static linter combining IEEE 1800-2023 compiler analysis (`MikePopoloski/slang`) with VeloSim's pure C# `velosim.rtl.v1` structural, FSM, Clock/Reset/CDC/RDC, DFT, SDC, 4-state, and concurrency analyzer (**45 built-in rules** across 4 ordered linting phases).

## Industry Linter Survey & Capability Matrix

| Capability | Verilator | `slang` | Verible / `svlint` | Aldec ALINT-PRO | Questa / Ascent / SpyGlass / Superlint | **VeloSim `lint`** |
|---|---|---|---|---|---|---|
| Single-pass multi-finding lint | Yes | Yes | Syntax only | Yes | Yes | **Yes (`RtlLinter` + `SlangLinter`)** |
| `-Wall` / `-Werror` / `-Wno-*` CLI | Yes | `-Weverything` | Flags / TOML | Tcl / GUI | Tcl / GUI | **Yes (Verilator + slang flags)** |
| **Phase-Based Linting (PBL)** + stop-on-phase-error | No | No | No | **Yes** | Partial | **Yes (`--phase rtl,fsm,cdc,style`, `--stop-on-phase-error`)** |
| **Compliance profiles & standard tags** | No | No | Rulesets | **DO-254, STARC, RMM, ISO 26262** | STARC, RMM, DO-254 | **Yes (`do254`, `starc`, `iso26262`, `lowrisc`, `signoff`, `fourstate` + custom `annotations`)** |
| **Automatic FSM extraction, reachability/deadlock & diagram export** | No | No | No | **Yes (FSM Viewer)** | Yes | **Yes (`FSMUNREACH`, `FSMDEADLOCK`, `FSMNORESET`, `FSMRECOVERY` + `--dump-fsm` JSON/Mermaid)** |
| **Clock/reset auto-detection, structural CDC/RDC & 2-FF sync recognition** | No | No | No | **Yes (ALDEC_CDC)** | Yes (separate tools) | **Yes (`CDCUNSYNC`, `RDCUNSYNC` + `--dump-cdc` JSON)** |
| **SDC timing constraint ingestion & audit** | No | No | No | **Yes (SDC)** | Yes | **Yes (`--sdc <file.sdc>`, `SDCUCONSTR`, `SDCOVERCONSTR`)** |
| **DFT clock/reset controllability checks** | No | No | No | **Yes (ALDEC_DFT)** | Yes | **Yes (`DFTCLKCTRL`, `DFTRSTCTRL`)** |
| **Auto-SVA assertion generation for CDC crossings** | No | No | No | Partial | **Yes (Superlint / Questa)** | **Yes (`--emit-cdc-sva <path.sv>`)** |
| **RTL Quality Score (0–100 + grade) & violation collation** | No | No | No | **Yes (Quality Metrics)** | **Yes (Collation)** | **Yes (`quality` score/grade + `--group-by rule\|signal\|file\|phase`)** |
| Project config (`velosim-lint.json`), scoped waivers & expiry audit | `.vlt` | `--waiver-file` | Limited | Project + waivers | `.awl` / `.swl` | **Yes (`UNUSEDWAIVER`, `EXPIREDWAIVER`, `reason`/`owner`/`expires`)** |
| Baseline / ratchet mode for legacy RTL | No | No | No | Waiver diff | Waiver diff | **Yes (`--write-baseline` / `--baseline`)** |
| SARIF 2.1.0 output (GitHub Code Scanning / VS Code) | Yes | No | No | Export | Wrapper | **Yes (`--sarif` with `properties.tags` & `phase`, NativeAOT-safe)** |
| True 4-state `X`/`Z` & concurrency race checks | 2-state only | Partial | No | Partial | Partial | **Yes (`FOURSTATE`, `CASEWITHX`, `RACEWW`, `RACERW`)** |
| Typed custom-rule API (Tier 2 C# / Tier 3 DSL) | C++ fork | C++ API | C++ / Rust fork | Rule Plug-in C/C++ | Tcl / C++ | **Yes (`IRtlLintRule` / `RtlLintContext`, ready for Cameo DSL)** |

## Quick Start

```sh
# 1. Lint SystemVerilog via slang (-Weverything) + VeloSim IR/FSM/CDC checks
VeloSim.Tools lint examples/rv32i_core.sv --top rv32i_core -Wall -Werror

# 2. Verilator-compatible --lint-only entry point
VeloSim.Tools --lint-only design.sv --top my_mod -Wall -Wno-WIDTHEXPAND

# 3. Safety-critical DO-254 / ISO 26262 / STARC / lowRISC compliance + SARIF 2.1.0
VeloSim.Tools lint design.rtl.json --profile do254 --sarif --output velosim.sarif
VeloSim.Tools lint design.rtl.json --profile starc --group-by phase

# 4. Phase-Based Linting (stop early on Phase 1 RTL errors before FSM/CDC/style noise)
VeloSim.Tools lint design.rtl.json --profile do254 --phase rtl,fsm,cdc,style --stop-on-phase-error

# 5. SDC-aware CDC/RDC analysis + FSM/CDC JSON dumps (with Mermaid) + SVA generation
VeloSim.Tools lint design.rtl.json --sdc constraints.sdc \
  --dump-fsm fsm.json --dump-cdc cdc.json --emit-cdc-sva cdc_assertions.sv

# 6. Generate a starter velosim-lint.json or inspect all 45 built-in rules
VeloSim.Tools lint --init-config velosim-lint.json
VeloSim.Tools lint --list-rules
VeloSim.Tools lint --list-rules --json

# 7. Baseline ratchet for legacy RTL (fail only on newly introduced issues)
VeloSim.Tools lint legacy.rtl.json -Wall --write-baseline lint-baseline.json
VeloSim.Tools lint legacy.rtl.json -Wall -Werror --baseline lint-baseline.json
```

## Phase-Based Linting (PBL)

Inspired by Aldec ALINT-PRO's Phase-Based Linting methodology, every rule belongs to one of four ordered phases so engineers resolve root-cause RTL bugs before chasing downstream CDC or style noise:

1. **`rtl`** (`LintPhase.RtlSynthesis`): Structural/synthesis correctness (`LATCH`, `UNOPTFLAT`, `MULTIDRIVEN`, `BLKANDNBLK`, `ASSIGNIN`, `INFLOOP`, `DIVZERO`, `SHIFTOVF`, `UNDRIVEN`, `WIDTHTRUNC`, `BLKSEQ`, `CASEINCOMPLETE`, `CASEOVERLAP`, `CASEX`, `CASEWITHX`, `SELRANGE`, `FOURSTATE`, `RACEWW`, `RACERW`, `VALIDERROR`).
2. **`fsm`** (`LintPhase.Fsm`): Automatic 1-process and 2-process FSM extraction and static verification (`FSMUNREACH`, `FSMDEADLOCK`, `FSMNORESET`, `FSMRECOVERY`).
3. **`cdc`** (`LintPhase.ClockResetCdc`): Clock/reset tree auto-detection, CDC/RDC, DFT controllability, and SDC audit (`CLKCOMB`, `SYNCASYNCNET`, `RESETCONSIST`, `CDCUNSYNC`, `RDCUNSYNC`, `DFTCLKCTRL`, `DFTRSTCTRL`, `SDCUCONSTR`, `SDCOVERCONSTR`).
4. **`style`** (`LintPhase.SyntaxStyle`): Coding style, unused objects, width/sign hygiene, naming, budgets, and waiver audits (`UNUSEDSIGNAL`, `UNUSEDMEM`, `WIDTHEXPAND`, `SIGNCOMPARE`, `IFDEPTH`, `SELFASSIGN`, `CMPCONST`, `NAMING`, `WIDTHBUDGET`, `UNUSEDWAIVER`, `EXPIREDWAIVER`).

Use `--phase rtl,fsm` to run a subset of phases, or `--stop-on-phase-error` (or `"stop_on_phase_error": true` in `velosim-lint.json`) to halt after the first phase containing an error and record `stopped_at_phase` in JSON output.

## Named & Compliance Profiles (`--profile`)

- **`default`**: Core structural, FSM reachability/deadlock, and CDC/RDC/SDC checks.
- **`wall`**: Equivalent to `-Wall` — enables all 45 built-in warnings (and forwards `-Weverything` to `slang`).
- **`signoff`** *(alias: `synthesis`, `rtl-signoff`)*: Enables `-Wall` and promotes synthesis/simulation-mismatch hazards (`LATCH`, `UNOPTFLAT`, `MULTIDRIVEN`, `BLKANDNBLK`, `CLKCOMB`, `ASSIGNIN`, `DIVZERO`, `INFLOOP`, `WIDTHTRUNC`, `BLKSEQ`, `CASEX`, `CASEWITHX`, `CASEINCOMPLETE`, `SYNCASYNCNET`, `RESETCONSIST`, `FSMDEADLOCK`, `CDCUNSYNC`) to errors.
- **`fourstate`** *(alias: `4state`)*: Enables `-Wall` and promotes 4-state and race hazards (`FOURSTATE`, `CASEX`, `CASEWITHX`, `RACEWW`, `RACERW`, `UNDRIVEN`, `DIVZERO`) to errors.
- **`do254`** *(alias: `do-254`, `avionic`)*: Safety-critical airborne hardware profile (DO-254) — enables `-Wall` and promotes structural, FSM (`FSMUNREACH`, `FSMDEADLOCK`, `FSMNORESET`, `FSMRECOVERY`), CDC/RDC (`CDCUNSYNC`, `RDCUNSYNC`), DFT (`DFTCLKCTRL`, `DFTRSTCTRL`), and SDC (`SDCUCONSTR`, `SDCOVERCONSTR`) hazards to errors.
- **`starc`** *(alias: `starc-rtl`)*: STARC RTL Design Style Guide profile — promotes `LATCH`, `UNOPTFLAT`, `MULTIDRIVEN`, `BLKANDNBLK`, `CLKCOMB`, `ASSIGNIN`, `BLKSEQ`, `CASEINCOMPLETE`, `CASEOVERLAP`, `CASEX`, `CASEWITHX`, `SYNCASYNCNET`, `RESETCONSIST`, `FSMUNREACH`, `FSMDEADLOCK`, `FSMNORESET`, `FSMRECOVERY`, `CDCUNSYNC`, `DFTCLKCTRL`, `DFTRSTCTRL` to errors.
- **`iso26262`** *(alias: `iso-26262`, `automotive`, `asil`)*: Automotive functional safety profile — promotes structural, FSM, CDC/RDC, and race hazards to errors.
- **`lowrisc`** *(alias: `opentitan`)*: Open-source silicon hygiene profile — promotes `LATCH`, `UNOPTFLAT`, `MULTIDRIVEN`, `BLKANDNBLK`, `CLKCOMB`, `ASSIGNIN`, `WIDTHTRUNC`, `BLKSEQ`, `CASEX`, `CASEWITHX`, `CDCUNSYNC`, `RDCUNSYNC`, `FSMRECOVERY`, `SIGNCOMPARE` to errors.

Every diagnostic carries its guideline traceability tags (e.g., `[DO-254, STARC-2.11.3.2, ISO-26262]`) plus any custom project `annotations` defined in `velosim-lint.json` — rendered in text output, JSON (`guideline_tags`), and SARIF 2.1.0 (`properties.tags`).

## Automatic FSM Extraction, CDC/RDC/DFT, SDC & Auto-SVA

- **FSM Extraction (`--dump-fsm <path.json>`):** Automatically detects 1-process and 2-process FSMs (state register, next-state combinational wire, reset state, all states, transitions), verifies reachability (`FSMUNREACH`), trap/deadlock states (`FSMDEADLOCK`), reset initialization (`FSMNORESET`), and default recovery (`FSMRECOVERY`), and emits both structured JSON and ready-to-render **Mermaid `stateDiagram-v2`** diagrams.
- **CDC / RDC / DFT (`--dump-cdc <path.json>`):** Automatically infers clock domains and reset domains from sequential edge lists, propagates combinational cones across domains, recognizes 1-bit **2-FF synchronizer chains**, flags unsynchronized 1-bit and multi-bit bus crossings (`CDCUNSYNC`), reset domain crossings (`RDCUNSYNC`), and internally generated clock/reset DFT hazards (`DFTCLKCTRL`, `DFTRSTCTRL`).
- **SDC Constraint Ingestion (`--sdc <file.sdc>` or `sdc_file` in `velosim-lint.json`):** Pure C# SDC parser supporting `create_clock`, `create_generated_clock`, `set_clock_groups -asynchronous|-physically_exclusive|-logically_exclusive`, `set_false_path -from ... -to ...`, and `set_reset`. Synchronous generated clocks and declared false paths suppress false-positive `CDCUNSYNC`, while unconstrained RTL clocks trigger `SDCUCONSTR` and stale SDC targets trigger `SDCOVERCONSTR`.
- **Auto-SVA Generation (`--emit-cdc-sva <path.sv>`):** Emits ready-to-bind SystemVerilog Assertions (`assert property`) for detected CDC crossings (synchronizer stability and multi-bit Gray-code checks).
- **RTL Quality Score & Collation (`--group-by`):** Every report computes a 0–100 RTL Quality Score and letter grade (`A+`–`F`), normalized by design element count, and supports grouping text output via `--group-by rule|signal|file|phase`.

## Tier 1: Project Configuration & Scoped Waivers (`velosim-lint.json`)

When `--config` is not passed (unless `--no-config` is specified), VeloSim automatically discovers `velosim-lint.json` in the target file's directory or current working directory. CLI flags always override config file settings.

```json
{
  "schema": "velosim.lint.config.v1",
  "profile": "do254",
  "warnings_as_errors": false,
  "warn_unused_waivers": true,
  "stop_on_phase_error": false,
  "phases": ["rtl", "fsm", "cdc", "style"],
  "sdc_file": "constraints.sdc",
  "slang_waiver_file": "slang-waivers.toml",
  "annotations": {
    "CDCUNSYNC": "CORP-CDC-001",
    "FSMDEADLOCK": "CORP-FSM-004"
  },
  "rules": {
    "WIDTHEXPAND": "off",
    "IFDEPTH": "warning",
    "FOURSTATE": "error"
  },
  "params": {
    "max_if_depth": 6,
    "unused_ignore_patterns": ["_*", "unused_*", "dbg_*"],
    "allow_literal_width_expand": true,
    "max_signal_width": 256,
    "max_memory_depth": 4096,
    "naming": {
      "signal_pattern": "^[a-z][a-z0-9_]*$",
      "clock_pattern": "^clk",
      "reset_pattern": "^rst"
    }
  },
  "waivers": [
    {
      "id": "W-001",
      "code": "UNUSEDSIGNAL",
      "file": "vendor/**",
      "signal": "dbg_*",
      "reason": "Silicon bring-up debug port",
      "owner": "rtl-team",
      "expires": "2027-06-01"
    }
  ]
}
```

- Waiver matching supports linear-time, ReDoS-safe glob wildcards (`*`, `?`, `**`) across `code`, `file`, `signal`, `context`, and `message`.
- Setting `"warn_unused_waivers": true` (or passing `--warn-unused-waivers`) emits `UNUSEDWAIVER` for waivers that matched 0 findings and `EXPIREDWAIVER` for waivers past their `YYYY-MM-DD` `expires` date.

## Tier 2 & Tier 3 Extensibility (`IRtlLintRule` & Cameo DSL Readiness)

Custom rules implement [`IRtlLintRule`](../src/VeloSim.Core/RtlLintExtensibility.cs) (or use [`RtlLintRuleRegistry`](../src/VeloSim.Core/RtlLintExtensibility.cs)) against [`RtlLintContext`](../src/VeloSim.Core/RtlLintExtensibility.cs), which exposes strongly-typed read-only tables of `Signals`, `Memories`, `Assignments`, `Statements`, `Expressions`, `Parameters`, and raw `Root` IR JSON.

```csharp
var registry = new RtlLintRuleRegistry()
    .Add("NO_WIDE_DIV", "Disallow division wider than 16 bits for area budget.", ctx =>
    {
        foreach (var expr in ctx.Expressions.Where(e => e.Kind == "div" && e.Width > 16))
            ctx.Report("NO_WIDE_DIV", LintSeverity.Error, $"Division of width {expr.Width} exceeds 16-bit area budget.", context: expr.Context);
    });

var report = RtlLinter.LintJson(irJson, new LintOptions(Wall: true, CustomRules: registry.Rules));
```

Every registered `IRtlLintRule` automatically inherits profiles, Phase-Based Linting, `velosim-lint.json` severity overrides, custom `annotations`, scoped waivers, baseline ratcheting, RTL Quality Score, JSON reports, and SARIF 2.1.0 export—providing a direct compilation target for Tier 3 **Cameo DSL** rules from `teal-cs`.
