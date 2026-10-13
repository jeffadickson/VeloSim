# UHDM native integration — stage 1

The experimental `native/uhdm/inspect.cpp` reads serialized UHDM using `UHDM::Serializer::Restore` and VPI-style traversal, then emits a JSON inventory of top modules, nested module instances, nets, registers, variables, processes and continuous assignments.

This is **not** a simulation IR or HDL runtime. No expressions, procedural semantics, event controls, or assignments are executed.

Build after compiling pinned UHDM:

```sh
cmake -S native/uhdm -B build/uhdm-inspect \
  -DUHDM_INCLUDE_DIR=/path/to/generated/include \
  -DUHDM_LIBRARY=/path/to/libuhdm.a
cmake --build build/uhdm-inspect
build/uhdm-inspect/velosim-uhdm-inspect /path/to/surelog.uhdm > inventory.json
python3 -m json.tool inventory.json
```

The UHDM C++ API calls were selected using upstream `util/uhdm-hier.cpp`. Only the isolated JSON-escape helper has been compiled and tested locally. The UHDM-dependent executable has **not** been compiled or run here because the UHDM checkout/build is unavailable in the terminal.

Next: validate against real Surelog/UHDM, implement typed expression/process lowering with explicit unsupported-node errors, then connect the resulting IR to the native scheduler.
