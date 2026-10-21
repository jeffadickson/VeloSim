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

