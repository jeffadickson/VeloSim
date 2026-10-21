# External interface development: 100 iterations

Request: ten local checkpoints, each after ten numbered development/qualification scenarios and an independent AI Verilog/VPI review; push only after all 100. These are bounded scenarios, not a claim of 100 new HDL features. Executable scenario IDs and descriptions are in `tests/native/external_cycles.h`; failures identify their iteration.

Baseline: bc6c4f7. Ninja builds use four jobs. Existing same-source Icarus differential tests and managed/native coverage remain final gates. Runtime/helper code is C++/C and C#; no Python helper is introduced.

| Checkpoint | Iterations | Work | Validation and review |
|---|---|---|---|
| 10 | 1–10 | VPI object identity, aliases, model/lifetime boundaries | 6/6 CTest pass; independent reviewer accepted identity/lifetime/model checks |

| 20 | 11–20 | Scalar/vector/unsigned metadata, top module and enclosing scope | 6/6 CTest pass; reviewer accepted with normalized width metadata caveat documented |

| 30 | 21–30 | Hexadecimal reads, four-state digit spelling and partial top groups | 6/6 CTest pass; independent review accepted Icarus-qualified four-state formatting |

| 40 | 31–40 | Octal reads including 64-bit and partial digit boundaries | 6/6 CTest pass; reviewer accepted group-of-three boundaries and X/Z semantics |

| 50 | 41–50 | Unsigned decimal reads, 32/64-bit boundaries and unknown values | 6/6 CTest pass; reviewer accepted unsigned decimal and unknown semantics |

| 60 | 51–60 | Bounded hexadecimal deposits, unknown extension, truncation and atomic failures | 6/6 CTest pass; reviewer accepted bounded parsing, extension and atomic failures |

| 70 | 61–70 | Bounded octal deposits, unknown extension and unsigned syntax | 6/6 CTest pass; reviewer accepted octal extension/truncation; managed 176 pass, 17 opt-in skipped |

| 80 | 71–80 | Decimal deposits, checked 64-bit overflow, X/Z and atomic syntax errors | 6/6 CTest pass; reviewer accepted checked unsigned decimal parsing |

| 90 | 81–90 | Finish/final scheduling, callback eval requests and clock-error recovery | 6/6 CTest pass; reviewer accepted finish guards and callback eval-request consumption |

| 100 | 91–100 | Radix callback payloads, scalar validation, vector/time boundaries; SDK relocation qualification | All 100 native scenarios and 6/6 CTest pass; both reviewers accepted; 5/5 SDK tests pass; GCC/Clang relocated SDK traces match Icarus for 140 cycles each |

## Review record

The independent Verilog/VPI reviewer accepted checkpoints 10, 20, 30, 40, 50, 60, 70, 80, 90 and 100. Reviews were read-only, separate from implementation. Checkpoint 20's declaration-shape caveat was added to the API documentation. The tooling reviewer independently identified absolute manifest paths and linked-output handling; both were corrected and have regression tests. Review acceptance is source review, not a substitute for execution evidence.

Iterations 1–100 are the executable `cycle(id, label, ...)` scenarios in the native external-interface test. Additional SDK and same-source integration checks qualify the final checkpoint. All builds use Ninja with four jobs. A direct managed test invocation initially lacked the native-library search path; the Make runner corrected it. Iteration 89 initially caught a missing request-consumption reset; the corrected implementation passes the regression.

Radix expectations were independently observed with Icarus 12 using a C VPI plugin against its installed header. The supported parser intentionally rejects decimal overflow, signs, whitespace and underscores rather than reproducing Icarus's more permissive conversion. No broader IEEE VPI or HDL language coverage is claimed.

Final checkpoint qualification: artifacts/external-interface-7e990c37caae4cf3b8752ade4a4147b8/report.json records same-source Icarus conformance and independent-header plugin compilation. GCC/Clang standalone builds outside the checkout both matched its 140-row trace. A relocated GCC host ran with LD_LIBRARY_PATH unset; readelf confirmed RUNPATH $ORIGIN. Final post-commit coverage, sanitizer and NativeAOT publication evidence is retained under artifacts and in the delivered outputs.
