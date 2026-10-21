# Fifty-iteration RTL expansion ledger

Baseline `2636dcc`. Inventory: [50 missing capabilities](verilog-missing-50.md). Each numbered iteration is a bounded implementation or qualification task. Builds are required at 10, 20, 30, 40 and 50; independent reviews at those same checkpoints. Local commits at 25 and 50 preserve the earlier user preference. No remote push.

| Iteration | Task | Evidence/status |
|---|---|---|
| 01 | Logical OR truth semantics and lowering | Implemented; checkpoint 10 qualification pending |
| 02 | Logical inequality, including X results | Implemented; checkpoint 10 qualification pending |
| 03 | Case equality with exact X/Z matching | Implemented; checkpoint 10 qualification pending |
| 04 | Case inequality with exact X/Z matching | Implemented; checkpoint 10 qualification pending |
| 05 | Unsigned less-than | Implemented; checkpoint 10 qualification pending |
| 06 | Unsigned less-or-equal | Implemented; checkpoint 10 qualification pending |
| 07 | Unsigned greater-than | Implemented; checkpoint 10 qualification pending |
| 08 | Unsigned greater-or-equal | Implemented; checkpoint 10 qualification pending |
| 09 | Bitwise XNOR | Implemented; checkpoint 10 qualification pending |
| 10 | Unary minus with destination context | Implemented; build/review/test checkpoint 10 pending |
| 11 | Logical left shift | Planned |
| 12 | Logical right shift | Planned |
| 13 | Unsigned arithmetic right shift | Planned |
| 14 | Reduction AND with known-zero dominance | Planned |
| 15 | Reduction NAND | Planned |
| 16 | Reduction OR with known-one dominance | Planned |
| 17 | Reduction NOR | Planned |
| 18 | Reduction XOR parity and unknowns | Planned |
| 19 | Reduction XNOR | Planned |
| 20 | Unary plus | Planned; build/review checkpoint 20 |
| 21 | Modular unsigned multiplication | Planned |
| 22 | Unsigned division, zero divisor gives X | Planned |
| 23 | Unsigned remainder, zero divisor gives X | Planned |
| 24 | Conditional expression with X/Z bit merging | Planned |
| 25 | Strict IR arity rejection for new operators | Planned; local commit |
| 26 | Independent native scalar truth qualification | Planned |
| 27 | Native reduction dominance/parity qualification | Planned |
| 28 | Shift amounts 0/width/63/64/65 and unknowns | Planned |
| 29 | Division/remainder zero and 64-bit boundaries | Planned |
| 30 | Conditional identical-Z and differing-bit merging | Planned; build/review checkpoint 30 |
| 31 | Actual source width pair 1/1 | Planned |
| 32 | Actual source width pair 1/8 | Planned |
| 33 | Actual source width pair 8/1 | Planned |
| 34 | Actual source width pair 7/9 | Planned |
| 35 | Actual source width pair 16/16 | Planned |
| 36 | Actual source width pair 31/32 | Planned |
| 37 | Actual source width pair 32/31 | Planned |
| 38 | Actual source width pair 63/64 | Planned |
| 39 | Actual source width pair 64/63 | Planned |
| 40 | Actual source width pair 64/64 | Planned; build/review checkpoint 40 |
| 41 | Same-source path/hash proof in operator reports | Planned |
| 42 | Stimulus, generated inputs and native trace fingerprints | Planned |
| 43 | Reference trace shape/order checks | Planned |
| 44 | Output trace negative qualification | Planned |
| 45 | CI includes operator matrix and evidence | Planned |
| 46 | Inventory disposition and supported-scope docs | Planned |
| 47 | Sanitizer checks for expanded runtime | Planned |
| 48 | Full managed/native regression and coverage gates | Planned |
| 49 | Final independent review and checkpoint build | Planned |
| 50 | Final local commit, NativeAOT bundle and evidence delivery | Planned; build checkpoint 50 |

Qualification iterations 26–50 are not additional language features. The planned feature scope is inventory items 1–24 for unsigned words of at most 64 bits. Arithmetic shifts of signed values remain part of the signed-types backlog.
