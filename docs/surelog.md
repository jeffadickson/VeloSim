# Surelog frontend experiment

VeloSim now has an optional Surelog/UHDM elaboration bridge. This is a frontend research path; **UHDM is not an executable simulation IR**. No SystemVerilog runtime support is implied by successful elaboration.

Surelog: https://github.com/chipsalliance/Surelog
UHDM: https://github.com/chipsalliance/UHDM

## Real smoke test

Install Surelog, then:

```bash
PYTHONPATH=src python3 -m velosim.surelog_frontend \
  --top surelog_fifo --threads 4 --output-dir build/surelog-fifo \
  examples/surelog_fifo.sv
```

Expected artifact: `build/surelog-fifo/slpp_all/surelog.uhdm` (the bridge searches under the output directory, as Surelog's layout may vary).

To inspect UHDM, use the `uhdm-dump` executable supplied by UHDM or its C++/Python VPI-style API. We deliberately do **not** parse binary UHDM with ad hoc Python.

## Engineering decision

- Surelog handles preprocessing, parsing, and elaboration.
- UHDM is a portable elaborated-design interchange format.
- VeloSim needs a separate lowering stage from UHDM objects to its own timed, four-state process IR.
- The production lowering stage should use the UHDM C++ API rather than assuming that the debug dump is stable.
- Python remains the verification orchestration layer; the native scheduler owns HDL execution.
- Preserve the slang bridge until Surelog wins conformance and integration benchmarks.

## Verification gates

1. Elaborate counter, FIFO, multiclock RTL, and a timed SV testbench using a real Surelog installation.
2. Walk module hierarchy, nets, processes, and expression nodes via UHDM.
3. Lower a counter into native VeloSim process IR and execute it.
4. Differentially compare Icarus/Verilator for applicable semantics.
5. Test frontend scalability with Surelog `-mt 1,2,4,8,16`.

The initial Python tests validate CLI invocation and artifact handling with a mock executable. They do **not** prove successful real Surelog elaboration.
