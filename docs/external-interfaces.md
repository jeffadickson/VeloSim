> Current development uses external model schema v2 and C++ word ABI 2 for RTL words through 256 bits. Rebuild hosts and generated models with matching headers and runtime. Previously compiled three-argument Model constructors cannot bind to the new runtime. See [current qualification and limits](procedural-types-arrays.md).

# Native external interfaces

VeloSim can export the structural RTL IR as a reusable `V<top>.h` C++ model and a shared VPI runtime. Both interfaces access the same design state. The structural backend supports bounded input-edge processes, elaborated hierarchy, signed or unsigned logic/bit words up to 128 bits, and fixed word memories. See [the current execution contract](open-items-100.md). General timed event scheduling remains unsupported. Rebuild SDK hosts and shared libraries together after this ABI metadata expansion.

## Generate and run the example

Run from the checkout in Linux/WSL with the pinned frontend installed. The exporter must already be built as described in the main README.

```sh
surelog -parse -elabuhdm -sverilog -mt 4 -odir artifacts/external-source -top external_counter examples/external_counter.sv
artifacts/with-uhdm/bin/velosim-uhdm-rtl artifacts/external-source/slpp_all/surelog.uhdm artifacts/external-model.json external_counter
artifacts/publish/VeloSim.Tools/VeloSim.Tools rtl-export artifacts/external-model.json --top external_counter --output-dir artifacts/external-sdk
cmake -S artifacts/external-sdk -B artifacts/external-build -G Ninja -DCMAKE_BUILD_TYPE=Release -DVELOSIM_HOST_SOURCE="$PWD/examples/external_main.cpp"
cmake --build artifacts/external-build --parallel 4
cc -std=c11 -shared -fPIC -I artifacts/external-sdk examples/external_plugin.c -o artifacts/external-plugin.so
artifacts/external-build/velosim_host "$PWD/artifacts/external-plugin.so" examples/external_stimulus.txt
```

`rtl-export` is implemented in C# and supports NativeAOT. Before publication, the same command can be run with `dotnet src/VeloSim.Tools/bin/Release/net10.0/VeloSim.Tools.dll`. Output must be empty, preventing accidental overwrite of host code. `external-model.json` records signal widths, directions, generated C++ member names and input hashes. The SDK contains the generated header, an IR snapshot, runtime sources, a VPI subset header and CMake files, so it can be built outside the checkout.

## C++ model API

```cpp
#include "Vexternal_counter.h"
Vexternal_counter top;
top.rst_n = 0;
top.enable = 1;
top.data = 42;
top.clk = 0; top.eval();
top.timeInc(1);
top.clk = 1; top.eval();
auto bits = top.sampled.bits();       // full-width 0/1/x/z string
auto known = uint64_t(top.q);         // throws on X/Z or values above UINT64_MAX
top.final();
```

Public ports are width-preserving `vsrtl::Port` proxies. Assign known values with `= uint64_t`, or use `.bits("...")` with exactly the declared width for X/Z. Outputs are read-only. Internal signals remain accessible through VPI lookup. HDL names colliding with C++ keywords, namespace names, runtime helpers or model methods receive safe names in the manifest. Model instances cannot be copied or moved.

The external clock starts at known zero. Other signals and memories start at X. Clock values must be known zero or one; each evaluated 0-to-1 transition executes one pre-edge snapshot and ordered NBA commit. Repeated `eval()` calls with a high clock do not retrigger. Every evaluation settles continuous assignments, including input-only changes without a clock edge. `timeInc()` advances a checked, unsigned 64-bit host time; it does not schedule HDL delays.

`evalNeeded()` indicates pending input drives or read-only callbacks. `gotFinish()` reports a VPI finish request or explicit finalization. `final()` dispatches end callbacks once; callers must invoke it explicitly while the complete model still exists. Destruction invalidates handles and unloads plugins without invoking callbacks. Models and their VPI handles are used on their owning thread.

The design follows Verilator's public-port and explicit [`eval()`/`final()` host pattern](https://verilator.org/guide/latest/connecting.html). It is a VeloSim API, without a `VerilatedContext`, `verilated.h`, DPI compatibility layer or Verilator binary compatibility.

## VPI subset

Call `top.useVpi()` before lookup or callback registration outside evaluation. `top.loadVpi(path)` loads a shared library exporting a null-terminated C `vlog_startup_routines` table before the first evaluation. Successful and failed-startup libraries remain loaded until model destruction, protecting any registered function pointers. Plugins using only supported symbols may be compiled against a standard simulator's VPI header. The integration test independently compiles the C example against Icarus's header.

Supported services:

- `vpi_handle_by_name`: `top.signal`, `TOP.top.signal`, top module names, and relative signal names under a module handle.
- `vpi_iterate`/`vpi_scan`: the top module, its ports and all scalar/packed signal words. `vpi_handle(vpiLowConn, port)` returns the signal. The structural IR exposes word signals uniformly as `vpiReg`; original source net subtype information is not retained.
- `vpi_get`: type, size and direction. `vpi_get_str`: local/full name and module definition name. `vpi_free_object` releases a handle; exhausted iterators release themselves.
- `vpi_get_value`/`vpi_put_value`: binary strings, scalar, 32-bit integer and aval/bval vectors. Vector bits follow standard encoding: 0=`00`, 1=`10`, X=`11`, Z=`01`. Binary deposits require the declared width and accept upper/lowercase X/Z. Integer reads containing X/Z and negative integer deposits wider than 32 bits are rejected; use vectors for those values.
- Immediate `vpiNoDelay` deposits on input ports only. Deposits outside callbacks change the input immediately; `eval()` propagates it. Deposits from value/start callbacks queue for evaluation, preserving the current settled observation. Start drives apply before the first evaluation; value callback drives apply on the next evaluation and set `evalNeeded()`.
- Value-change, start, end and zero-delay read-only callbacks. Changes are observed once per explicit evaluation after NBA commit and settle; intermediate glitches are coalesced. Read-only callbacks registered from value callbacks run in that evaluation's read-only phase. Read-only callbacks registering more read-only callbacks defer them to the next evaluation. Deposits from the read-only phase are rejected. Callback removal is honored during dispatch, and each invocation selects its owning model context.
- `vpi_get_time` with `vpiSimTime`, `vpi_control(vpiFinish)`, `vpi_get_vlog_info`, `vpi_printf`, and `vpi_chk_error` diagnostics. Checked API failures never propagate C++ exceptions through the C interface. A no-delay put returns null on success as no scheduled event is created; check `vpi_chk_error` for failure.

There are bounds of 65,536 retained handles per thread, 64 loaded plugins per model and 256 startup routines per plugin. Retained handle tokens prevent reuse of a destroyed object's address from turning a stale handle into a new object. Separate models have independent state, time and callbacks; ordinary C plugin global variables remain shared according to the dynamic loader's rules.

Unsupported services include timed/force/release puts, read-write synchronization callbacks, system-task/function registration, VPI memory-element access, arbitrary hierarchy and a full event-region scheduler. Unsupported exported operations return a checked error; a plugin requiring an unavailable symbol fails during loading. Existing delay-free standalone trace executables remain available.

## Qualification

`VELOSIM_REAL_FIFO=1 dotnet test tests/VeloSim.Tests -c Release -m:4 --filter FullyQualifiedName~ExternalInterfaceTests` builds actual RTL through Surelog, exports the reusable model, builds it with Ninja, loads a C plugin compiled against Icarus's independent header and compares 140 complete output traces against Icarus compiling the identical design file. It also compiles a model with problematic C++ port names. Reports under `artifacts/external-interface-*` record unchanged source/build inputs, tool versions, commands and artifact hashes.

The native `external_interfaces` CTest checks handle lifetime and malformed requests, X/Z vector boundaries, public port conversion, repeated high-clock evaluation, model isolation, thread ownership, startup/removal ordering, first-evaluation callback registration, callback context pinning, deferred drives, read-only phases and explicit finalization. The VPI shared library and test plugin are included in LLVM coverage measurement and source/binary evidence. The installed/native bundle contains `libvelosim_vpi` and the external API headers.

Additional object services: `vpi_compare_objects` compares object identity across independently acquired handles. `vpi_handle(vpiScope, signal/port)` and `vpi_handle(vpiModule, signal/port)` return the top module; the top has no enclosing scope. `vpiTopModule` is true on the top. `vpiSigned` reports the normalized declaration signedness. `vpiScalar`/`vpiVector` reflect normalized width 1/>1; the IR does not retain whether width one originally used a packed declaration.

Radix reads and callback payloads support `vpiHexStrVal`, `vpiOctStrVal` and `vpiDecStrVal` (signed when the declaration is signed). Hex/octal output preserves declared width in digits; a wholly X/Z digit uses lowercase and a mixed digit uses uppercase X/Z, matching the independently tested Icarus behavior. Decimal output uses x/z for wholly unknown values and X/Z for mixed values, with X taking precedence when both occur.

Radix deposits use a bounded unsigned subset: hex accepts 1–32 digits, octal 1–43 digits, with 0–9/a–f as appropriate and case-insensitive X/Z. Leading X/Z extends through the port; leading known digits zero-extend. Excess value bits truncate to the declared width. Characters are literal radix digits; there is no prefix interpretation. Signs, whitespace and underscores are rejected. Decimal accepts 1–39 unsigned digits within the 128-bit range or a single X/Z; numeric overflow is rejected rather than wrapped. A valid 128-bit value truncates when assigned to a narrower port. Parsing errors leave the input unchanged. Scalar callback requests on wider signals fail during registration.

`requestEval()` can request another pass from a callback; `evalNeeded()` remains true until that pass consumes the request. After `finish()`/`final()`, it returns false and time advances, evaluation requests and new plugin loads are rejected.

Exported SDK manifests use paths relative to the SDK directory and hash all eight generated/copied inputs, including a standalone README. The manifest does not hash itself or later user additions. Empty linked output directories and linked parents are rejected. On Linux, the generated CMake build uses an origin-relative runtime path so the executable and shared runtime can move together. Linux/WSL is the qualified platform.

The compatibility checks use [Icarus's independent standard VPI header](https://github.com/steveicarus/iverilog/blob/master/vpi_user.h) and actual Icarus runs. This remains a bounded VPI subset and explicit host-evaluation model, as described above.
