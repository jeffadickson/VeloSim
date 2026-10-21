# External interface development: 100 iterations

Request: ten local checkpoints, each after ten numbered development/qualification scenarios and an independent AI Verilog/VPI review; push only after all 100. These are bounded scenarios, not a claim of 100 new HDL features. Executable scenario IDs and descriptions are in `tests/native/external_cycles.h`; failures identify their iteration.

Baseline: bc6c4f7. Ninja builds use four jobs. Existing same-source Icarus differential tests and managed/native coverage remain final gates. Runtime/helper code is C++/C and C#; no Python helper is introduced.

| Checkpoint | Iterations | Work | Validation and review |
|---|---|---|---|
| 10 | 1–10 | VPI object identity, aliases, model/lifetime boundaries | 6/6 CTest pass; independent reviewer accepted identity/lifetime/model checks |

