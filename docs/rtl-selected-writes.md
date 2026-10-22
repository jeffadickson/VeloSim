# Indexed selections and selected nonblocking assignments

The structural backend now lowers indexed packed reads and selected-word nonblocking writes through the actual Surelog/UHDM path. It preserves the existing one-posedge-process, unsigned four-state, 1–64-bit word boundary. This extends the structural backend, not the older flat-process counter frontend.

## Accepted forms

| Form | Read | Nonblocking target |
|---|---|---|
| `word[index]` | Existing support | Added |
| `word[left:right]` | Existing support | Added |
| `word[base +: width]` | Added | Added |
| `word[base -: width]` | Added | Added |

Packed declarations remain canonical `[W-1:0]`. Fixed bounds must be materialized, nonnegative, descending constants. Indexed widths must be materialized known constants from 1 through 64. The HDL exporter currently requires a declared packed source word for indexed reads. Selected targets must refer directly to a writable declared word. Selected memory elements, nested/computed targets, input/clock writes, selected continuous-assignment targets and conflicting continuous/sequential writers remain rejected.

## Index domain and out-of-range behavior

Known bases for indexed reads and selected writes are restricted at runtime to `0..2147483647` (`INT32_MAX`). Wide unsigned index expressions/ports are accepted, but a larger known value raises an explicit unsupported-range error. It is not truncated, wrapped into a negative address, or reported as a successful HDL comparison. Unknown X/Z indices bypass that numeric bound and use the semantics below. Existing ordinary bit-read semantics are unchanged.

Within that qualified domain:

- An unknown indexed-read base produces an all-X result.
- Each out-of-range read position produces X; in-range positions preserve 0/1/X/Z.
- An unknown selected-write base ignores that selected update.
- A partially out-of-range write changes only valid destination positions and preserves the rest of the destination word. Fully out-of-range selected writes have no effect.
- Plus selection result bit `j` refers to `base+j`; minus selection result bit `j` refers to `base-(width-1-j)`. The per-bit implementation handles partial overlap below zero without unsigned underflow.

For an eight-bit word, `word[0 -: 4]` reads bit zero into result bit three and returns X in the other three positions. A corresponding write can change bit zero using RHS bit three. A minus selection starting above the source MSB can still overlap its highest bits.

The value-domain restriction follows a retained reference-tool discrepancy: both the installed Icarus 12 and an isolated Icarus 13 build returned a partial negative-index read for an unsigned all-ones procedural base. See the [investigation and iteration ledger](rtl-100-cycles.md). The original failure is retained, and native tests now require explicit rejection of unsupported extreme values. No universal reference-tool fix or complete LRM analysis is claimed.

## Ordered NBA behavior

For a declared 64-bit `q`, a four-bit `replacement`, and a legal unsigned `i`:

```systemverilog
always_ff @(posedge clk) begin
  q <= data;
  q[i +: 4] <= replacement;
  snapshot <= q;
end
```

All RHS expressions and dynamic indices read pre-edge state. The first assignment stages a whole word, the second merges four selected bits into that staged word, and the last assignment stages the old `q` into `snapshot`. Reversing the first two statements lets the later whole-word assignment win. Multiple partial updates merge in source order; the last assignment to an overlapping bit wins within this single process.

RHS context uses the selected width, rather than the width of the parent word. This matters for operations such as shifts of a narrow operand. Both generated standalone executables and reusable C++ models use the same operations. A known index outside the supported domain throws before the staged sequential updates commit; reusable-model qualification checks unchanged stored state and recovery on a subsequent valid evaluation.

The IR represents an indexed selection as:

```json
{
  "kind": "indexedselect",
  "value": { "kind": "ref", "name": "q" },
  "base": { "kind": "ref", "name": "i" },
  "width": 4,
  "descending": true
}
```

It may appear as an expression or as the target of an `nba` statement. IR validation checks exact fields, widths, references, target ownership, combinational dependencies and writer conflicts. Generated public ports named `select_indexed` or `deposit_indexed` receive safe C++ member aliases recorded in the SDK manifest.

## Reproduce the qualification

With the pinned Surelog/UHDM installation and built structural exporter available, run from the checkout in Linux/WSL:

```sh
VELOSIM_REAL_FIFO=1 dotnet test tests/VeloSim.Tests -c Release -m:4 \
  --filter 'FullyQualifiedName~RtlIndexed'
```

`VELOSIM_SURELOG` and `VELOSIM_UHDM_RTL` select the frontend/exporter. Optional `VELOSIM_INDEXED_IVERILOG` and `VELOSIM_INDEXED_VVP` select a matching reference pair; installed Icarus is the default. Source, compiler and tool hashes and reference versions are retained under `artifacts/rtl-indexed-*`.

Ten mixed-width fixtures exercise words of 1, 7, 8, 16, 31, 32, 33, 63 and 64 bits, with selected widths through 64 and a four-bit target on a one-bit word. Each fixture runs 128 directed/seeded known and X/Z vectors against Icarus using the identical generated design `.sv` path. Complete traces are compared at worker settings 1/4/8 and through the generated reusable SDK. Named host helper collisions, repeated high-clock evaluations and error recovery are included. These settings do not establish parallel execution: the single process remains serial.

The three additional directed actual-source fixtures each have ten vectors for indexed reads, bit/fixed writes, and indexed writes. The [100-cycle ledger](rtl-100-cycles.md) distinguishes these scenarios from IR validation and independent native string-oracle tests. Final coverage and publication evidence is retained in the delivered validation report; upstream dependencies and the UHDM exporter remain outside the default LLVM kernel/runtime denominator.
