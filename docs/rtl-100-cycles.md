# Indexed selections and selected nonblocking writes: 100 cycles

Baseline `7f4b31f`. A fresh batch of 100 bounded implementation/qualification scenarios, with a local commit and independent AI Verilog review at every tenth checkpoint. Push only after all final gates pass. Numbered scenarios are recorded in native and managed tests; these are not 100 new language features.

Scope: unsigned canonical `[W-1:0]` words up to 64 bits, one known-clock posedge process. Target indexed `+:`/`-:` reads and selected-word nonblocking writes, preserving X/Z, partial out-of-range behavior and ordered NBA overlap. Signed indices, nested/memory-element selected targets, arbitrary hierarchy and general scheduling remain outside scope.

| Checkpoint | Cycles | Work | Validation/review |
|---|---|---|---|
| 10 | 1–10 | Indexed-read runtime, checked arithmetic and independent string oracle | 6/6 native suites pass; independent reviewer accepted bounds, orientation and oracle |
| 20 | 11–20 | Indexed-read IR shape, sizing and dependency validation | Planned |
| 30 | 21–30 | Actual UHDM indexed reads against same-source Icarus | Planned |
| 40 | 31–40 | Selected-write runtime, mask replacement and partial bounds | Planned |
| 50 | 41–50 | Bit/fixed-part NBA IR and code generation | Planned |
| 60 | 51–60 | Actual selected-word NBA qualification | Planned |
| 70 | 61–70 | Indexed-word NBA runtime/IR qualification | Planned |
| 80 | 71–80 | Actual indexed-word NBA qualification | Planned |
| 90 | 81–90 | Writer conflicts, invalid targets and ordered overlap | Planned |
| 100 | 91–100 | Mixed-width actual RTL matrix and final documentation | Planned |

