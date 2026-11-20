# Associative arrays: implementation and qualification in progress

The C# frontend validates the normalized array metadata and emits C++ calls. The native runtime stores cells in `std::map<Bits,W>`; it does not implement a separate tree. Standard-library `find`, `erase`, `upper_bound` and `lower_bound` provide storage and ordered lookup. The small adapter supplies HDL semantics: key sizing, signed numeric ordering, four-state missing values, two-state coercion and resource limits. The independent C# oracle uses `SortedDictionary<BigInteger,byte>` with mathematical keys rather than the runtime key encoding.

Implemented scope is integral keys and packed elements of 1–256 bits, with at most 256 populated entries per map. Existing cells remain writable at capacity. Missing reads do not create cells. Unknown keys do not insert or delete cells. `num`, `exists`, keyed/all deletion and `first/last/next/prev` have adapters; failed traversal preserves its index. Generated SDK models snapshot the separate associative inventory for evaluation rollback. End-to-end rollback qualification is still pending.

String and wildcard keys, nested associative arrays and aggregate element support remain open. General timed scheduling remains open. These changes do not close the overall SystemVerilog associative-array requirement.

## Reference-tool limitations observed locally

The installed Verilator is 5.020. Directed comparisons exposed two differences from the independent expected behavior:

* A negative signed key sorts after nonnegative keys in this version's generated associative container. Comparative signed-key scenarios therefore use nonnegative keys; direct native tests check negative signed ordering independently.
* A missing unguarded read creates a default entry in this version, changing subsequent `num()`. Comparative HDL guards reads with `exists`; native tests explicitly verify that missing reads leave the inventory unchanged.

Direct native tests also cover traversal from an absent starting index, because this installed reference uses `find` for `next/prev` and fails when that starting key is absent. Restricting a differential comparison is not evidence that the excluded behavior agrees with the reference.

## Current evidence

The current native associative CTest passed 1/1, including signed negative ordering, absent reads, unknown keys, wide words, traversal and the capacity boundary. The current focused managed run passed 14/14 (0 skipped), including two 26-row HDL comparisons, standalone worker settings 1/4/8, SDK traces and rollback, negative contracts and the FsCheck property. Coverlet measured RtlAssociative.cs at 34/35 executable lines (97.14%) and 59/68 branch outcomes (86.76%). This narrow managed-file scope excludes the native frontend/runtime; whole Core coverage for the focused run was 16.08% lines and 12.16% branches. Native coverage totals, mutation checks, broader regressions and final requirement evidence remain pending. Raw diagnostic runs and logs are retained under the native WSL `~/.local/share/VeloSim/assoc-timing-evidence` directory. No full conformance or final verification claim is made.

Library references: [C++ map](https://learn.microsoft.com/en-us/cpp/standard-library/map-class?view=msvc-170) and [.NET SortedDictionary](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.sorteddictionary-2?view=net-10.0). The optional private UHDM precompiled-header build uses [CMake target_precompile_headers](https://cmake.org/cmake/help/latest/command/target_precompile_headers.html); it defaults off and does not change consumer requirements.