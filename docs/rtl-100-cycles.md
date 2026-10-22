# Indexed selections and selected nonblocking writes: 100 cycles

Baseline `7f4b31f`. A fresh batch of 100 bounded implementation/qualification scenarios, with a local commit and independent AI Verilog review at every tenth checkpoint. Push only after all final gates pass. Numbered scenarios are recorded in native and managed tests; these are not 100 new language features.

Scope: unsigned canonical `[W-1:0]` words up to 64 bits, one known-clock posedge process. Target indexed `+:`/`-:` reads and selected-word nonblocking writes, preserving X/Z, partial out-of-range behavior and ordered NBA overlap. Signed indices, nested/memory-element selected targets, arbitrary hierarchy and general scheduling remain outside scope.

| Checkpoint | Cycles | Work | Validation/review |
|---|---|---|---|
| 10 | 1–10 | Indexed-read runtime, checked arithmetic and independent string oracle | 6/6 native suites pass; independent reviewer accepted bounds, orientation and oracle |
| 20 | 11–20 | Indexed-read IR shape, sizing and dependency validation | 10/10 managed cases and 6/6 native suites pass; reviewer naming finding fixed and accepted |
| 30 | 21–30 | Actual UHDM indexed reads against same-source Icarus | 2/2 actual frontend tests, 10 supported-domain vectors at workers 1/4/8, and 6/6 native suites pass; revised value-domain policy reviewed and accepted |
| 40 | 31–40 | Selected-write runtime, mask replacement and partial bounds | Cycles 31–40 and 6/6 native suites pass; independent reviewer accepted masks, bounds and helper naming |
| 50 | 41–50 | Bit/fixed-part NBA IR and code generation | Planned |
| 60 | 51–60 | Actual selected-word NBA qualification | Planned |
| 70 | 61–70 | Indexed-word NBA runtime/IR qualification | Planned |
| 80 | 71–80 | Actual indexed-word NBA qualification | Planned |
| 90 | 81–90 | Writer conflicts, invalid targets and ordered overlap | Planned |
| 100 | 91–100 | Mixed-width actual RTL matrix and final documentation | Planned |



## Reference-simulator compatibility finding

The first actual comparison retained a failure at `artifacts/rtl-indexed-reads-b598617b92424d199d38035c427285fe`: Icarus 12 interpreted an unsigned all-ones indexed base as a negative address and returned a partial word, while VeloSim's checked unsigned arithmetic returned all X. A separate Icarus 13.0 build at `dfeee909ed9f20b4870dd93423156c0170c0e1ff` reproduced the procedural discrepancy. An upstream overflow guard in [vvp/part.cc](https://github.com/steveicarus/iverilog/blob/dfeee909ed9f20b4870dd93423156c0170c0e1ff/vvp/part.cc#L229) covers another path; it did not resolve this test. Both failing logs and reproductions are retained. We do not claim a complete LRM analysis or universal Icarus fix.

The qualified value domain is therefore explicit: known indexed-selection bases are `0..INT32_MAX`; larger known bases throw before a selected update commits. Unknown bases retain all-X read / ignored-write semantics. Wide unsigned index ports remain accepted, but callers must respect this runtime value bound. Native scenario 7 checks rejection of the original extreme values; actual scenario 27 compares the largest supported known base. This is a documented conservative execution limit, not a reinterpretation of unsigned values as negative and not a claim of agreement outside the supported domain.

`VELOSIM_INDEXED_IVERILOG` and `VELOSIM_INDEXED_VVP` optionally select a reference pair. The normal installed Icarus remains the default; no CI toolchain replacement is required. VeloSim builds use Ninja with four jobs. The isolated Icarus investigation used its upstream Autotools/Makefiles with `make -j4`, installed only under the user's local prefix.


