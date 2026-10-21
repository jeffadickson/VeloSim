# Fifty-iteration RTL expansion ledger

Baseline `2636dcc`. Inventory: [50 missing capabilities](verilog-missing-50.md). Each numbered iteration is a bounded implementation or qualification task. Builds are required at 10, 20, 30, 40 and 50; independent reviews at those same checkpoints. Local commits at 25 and 50 preserve the earlier user preference. No remote push.

| Iteration | Task | Evidence/status |
|---|---|---|
| 01 | Logical OR truth semantics and lowering | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 02 | Logical inequality, including X results | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 03 | Case equality with exact X/Z matching | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 04 | Case inequality with exact X/Z matching | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 05 | Unsigned less-than | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 06 | Unsigned less-or-equal | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 07 | Unsigned greater-than | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 08 | Unsigned greater-or-equal | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 09 | Bitwise XNOR | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 10 | Unary minus with destination context | Checkpoint 10 operator test pass; reviewed; final signedness repair confirmed at 20 |
| 11 | Logical left shift | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 12 | Logical right shift | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 13 | Unsigned arithmetic right shift | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 14 | Reduction AND with known-zero dominance | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 15 | Reduction NAND | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 16 | Reduction OR with known-one dominance | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 17 | Reduction NOR | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 18 | Reduction XOR parity and unknowns | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 19 | Reduction XNOR | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 20 | Unary plus | Checkpoint 20 build and all four actual integration tests pass after signedness repair |
| 21 | Modular unsigned multiplication | Checkpoint 30 actual operator/arity tests pass; checkpoint-25 commit 40c16ad |
| 22 | Unsigned division, zero divisor gives X | Checkpoint 30 actual operator/arity tests pass; checkpoint-25 commit 40c16ad |
| 23 | Unsigned remainder, zero divisor gives X | Checkpoint 30 actual operator/arity tests pass; checkpoint-25 commit 40c16ad |
| 24 | Conditional expression with X/Z bit merging | Checkpoint 30 actual operator/arity tests pass; checkpoint-25 commit 40c16ad |
| 25 | Strict IR arity rejection for new operators | Checkpoint 30 actual operator/arity tests pass; checkpoint-25 commit 40c16ad |
| 26 | Independent native scalar truth qualification | Native boundary/oracle checks and all five CTests pass; checkpoint 30 reviewed |
| 27 | Native reduction dominance/parity qualification | Native boundary/oracle checks and all five CTests pass; checkpoint 30 reviewed |
| 28 | Shift amounts 0/width/63/64/65 and unknowns | Native boundary/oracle checks and all five CTests pass; checkpoint 30 reviewed |
| 29 | Division/remainder zero and 64-bit boundaries | Native boundary/oracle checks and all five CTests pass; checkpoint 30 reviewed |
| 30 | Conditional identical-Z and differing-bit merging | Native boundary/oracle checks and all five CTests pass; checkpoint 30 reviewed |
| 31 | Actual source width pair 1/1 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 32 | Actual source width pair 1/8 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 33 | Actual source width pair 8/1 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 34 | Actual source width pair 7/9 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 35 | Actual source width pair 16/16 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 36 | Actual source width pair 31/32 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 37 | Actual source width pair 32/31 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 38 | Actual source width pair 63/64 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 39 | Actual source width pair 64/63 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 40 | Actual source width pair 64/64 | Checkpoint 40 pass: all eleven width pairs, 201 cycles each, workers 1/4/8; reviewed |
| 41 | Same-source path/hash proof in operator reports | Checkpoint 40 strict report/interface/trace tests pass; final tooling review accepted |
| 42 | Stimulus, generated inputs and native trace fingerprints | Checkpoint 40 strict report/interface/trace tests pass; final tooling review accepted |
| 43 | Reference trace shape/order checks | Checkpoint 40 strict report/interface/trace tests pass; final tooling review accepted |
| 44 | Output trace negative qualification | Checkpoint 40 strict report/interface/trace tests pass; final tooling review accepted |
| 45 | CI includes operator matrix and evidence | Checkpoint 40 strict report/interface/trace tests pass; final tooling review accepted |
| 46 | Inventory disposition and supported-scope docs | Inventory, scope documentation and public FIFO operator example added |
| 47 | Sanitizer checks for expanded runtime | All five address/undefined-behavior sanitizer CTests pass |
| 48 | Full managed/native regression and coverage gates | Final committed-source coverage command records managed/native test counts and separate gates in delivered Validation.txt |
| 49 | Final independent review and checkpoint build | Both final independent AI reviews accepted; no open blocker |
| 50 | Final local commit, NativeAOT bundle and evidence delivery | Local final source checkpoint; build 50, NativeAOT bundle and same-source FIFO CLI evidence recorded in delivered Validation.txt |

Qualification iterations 26–50 are not additional language features. The planned feature scope is inventory items 1–24 for unsigned words of at most 64 bits. Arithmetic shifts of signed values remain part of the signed-types backlog.

## Build and repair evidence

Checkpoint builds use `make build JOBS=4` and the pinned UHDM target uses `cmake --build artifacts/with-uhdm --parallel 4`; both use Ninja for native compilation. Build logs are retained as `build10.log`, `build20.log`, `build30.log`, `build40.log` and `build50.log` in the delivered output directory. Checkpoint 20 required signedness repairs after actual negative tests failed; the corrected full actual integration selection passed 4/4. Checkpoint 30 detected a stale exporter caused by editing while an earlier compile was in progress; a forced current-source rebuild then passed both operator tests and all five native CTests. These failed attempts are retained, not counted as passes.

Checkpoint 40 passed 13/13 tests: eleven width pairs and two strict negative qualification tests. The width pairs are 8/16 plus the ten pairs in iterations 31–40. Each uses 201 vectors and compares full traces at worker settings 1/4/8. Five native CTests also pass with address/undefined-behavior sanitizers.

The final source checkpoint precedes committed-source measurement and publication so version metadata and evidence agree. The delivered `Validation.txt`, coverage `gate.json`, native bundle `build.txt` and CLI `fifo-report.json` record the final command outcomes. No claim of passing final gates is made before those commands succeed. No remote push is part of this batch.

The first full final-checkpoint run passed 185/186 tests and exposed an unmaterialized negative parameter being converted to zero. The reviewed exporter repair rejects empty parameter values. All twelve targeted rejection variants then passed, and the operator-based actual FIFO passed 1,043 cycles against Icarus and the independent queue. Final source checkpoint is amended with this repair before fresh measurement/publication.
