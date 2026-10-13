# slang front end — integration stage 1

VeloSim delegates parsing, type checking, parameterization and elaboration to **MikePopoloski/slang** (MIT license). The initial bridge invokes the official slang CLI with `--std 1800-2023 --ast-json <path> --ast-json-source-info`, verifies successful completion and reads its elaborated AST. No slang source is vendored.

## Usage

Install slang from https://github.com/MikePopoloski/slang/releases, then:

```bash
PYTHONPATH=src python -m velosim.slang_frontend --top slang_counter examples/slang_counter.sv
```

Optional `--ast-out build/counter-ast.json` requires an existing build directory. `VELOSIM_SLANG=/path/to/slang` selects another executable.

## Boundary

This is a real frontend integration bridge, **not simulation**. Slang AST JSON is a diagnostic/prototyping format, not a stable production ABI. Next: integrate slang C++ AST APIs, lower elaborated instances/processes/expressions into a versioned VeloSim IR, and execute the IR on the native scheduler. No runtime SystemVerilog compliance is claimed.

## Tests

Unit tests use a mock executable to test invocation, errors, JSON validation and shell safety. When slang is installed, run the command above as a real smoke test. CI should pin a slang release before marking this integration fully verified.
