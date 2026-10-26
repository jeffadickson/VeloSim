# Container and timed-event runtime foundation

This change adds internal native primitives. It does not yet accept dynamic arrays,
associative arrays, queues or delayed processes in HDL. Existing exporter rejection
and generated-model behavior remain in place. The next implementation step is a
typed IR with container ownership and suspended-process state, followed by UHDM
lowering and same-input HDL differential tests.

`native/rtl/containers.h` provides owned, copyable integral-element storage with
1–256-bit elements and a 256-element resource bound. Dynamic allocation optionally
preserves the existing prefix; ordinary allocation initializes all cells. Four-state
elements default to X and two-state elements to zero. Invalid accesses return the
element default and ignore writes. Queues support front/back push/pop, indexed
reads/writes and deletion of all elements. Associative storage uses canonical unsigned
256-bit keys, lookup, insertion, existence, erase and clear. Keys cannot contain X/Z.
These primitives do not implement signed/string key ordering, associative traversal,
queue slicing/insertion/deletion by index, bounded-queue truncation or references
into storage. Resource overflow raises an exception and preserves existing contents.

`native/rtl/timed_events.h` provides a bounded deterministic transport queue. It
orders opaque process IDs by unsigned tick, delta, region and insertion order.
Regions are active, inactive and NBA. Zero-delay events targeting an earlier region
enter a new delta; positive delays start at delta zero of the future tick. The queue
checks time overflow and pending capacity before mutation and imposes a lifetime
execution budget so repeated drain/refill cannot bypass the guard. It does not execute
callbacks, suspend HDL processes, perform signal updates, wake sensitivity lists or
implement the complete SystemVerilog scheduler region algorithm. Host-driven clocks
and the existing single-call RTL evaluator are unchanged. The external word ABI
remains 2; rebuild older C++ hosts using matching headers/runtime.

Verification uses native CTest, Clang LLVM source coverage, ASan/UBSan and a seeded
10,000-operation queue oracle plus 1,000 randomized timestamp/region events
(seed 20261030). Negative cases cover invalid widths/regions, capacities, missing
keys, empty queues, invalid indices, timestamp overflow and exhausted budgets.
`tools/ContainerEventMutationProbe.cs` compiles isolated copies and checks five
semantic faults: lost allocation prefix, reversed queue direction, erased map keys,
reversed same-region insertion order and disabled event execution budget. Only
successful compilations followed by the explicit assertion marker count as detected
faults. This is focused mutation testing, not an exhaustive mutation score.

Evidence is retained in ignored `artifacts/container-events/`. Focused coverage
applies to these two headers, not the existing HDL exporter, generated-model
instructions or the C# mutation helper's own error paths. Prior full-suite managed
coverage does not qualify new source automatically.

Final native and ASan/UBSan CTest runs each pass seven tests. Focused LLVM 18
coverage measures 64/64 lines (100%) and 56/58 branches (96.55%) across the two new
headers, without export diagnostics. The uncovered branches are defensive delta
and insertion-sequence overflow rejections, which cannot be reached within the
normal lifetime budget. NativeAOT mutation controls pass and all five compiled
semantic faults are detected. A read-only AI peer reviewed the source and scope;
this is not human expert approval. StrictDoc export passes for the two narrow
runtime requirements while broad HDL array/scheduling requirements stay open.

Reproduce from the WSL checkout with Clang/LLVM 18:

```sh
cmake -S . -B artifacts/container-events/build -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug -DCMAKE_C_COMPILER=clang-18 \
  -DCMAKE_CXX_COMPILER=clang++-18 -DVELOSIM_COVERAGE=ON
cmake --build artifacts/container-events/build --parallel 4
ctest --test-dir artifacts/container-events/build --output-on-failure
LLVM_PROFILE_FILE="$PWD/artifacts/container-events/focused.profraw" \
  artifacts/container-events/build/bin/velosim_container_event_tests
llvm-profdata-18 merge -sparse artifacts/container-events/focused.profraw \
  -o artifacts/container-events/focused.profdata
llvm-cov-18 report artifacts/container-events/build/bin/velosim_container_event_tests \
  -instr-profile=artifacts/container-events/focused.profdata \
  native/rtl/containers.h native/rtl/timed_events.h
dotnet publish tools/ContainerEventMutationProbe.cs -c Release -r linux-x64 \
  -p:PublishAot=true -p:VeloSimNativeJobs=4 -o artifacts/container-events/mutation-tool
artifacts/container-events/mutation-tool/ContainerEventMutationProbe "$PWD"
```

Use a separate Ninja build directory with `VELOSIM_SANITIZERS=ON` for ASan/UBSan.
