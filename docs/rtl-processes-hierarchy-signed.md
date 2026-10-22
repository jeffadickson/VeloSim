# Shared-clock processes, hierarchy and signed words

This milestone implements the three priorities from the preceding development assessment: multiple clocked processes sharing one clock, bounded elaborated hierarchy, and signed arithmetic with wider words. It does not implement the entire SystemVerilog language.

## Execution

The structural backend accepts up to 256 `always_ff @(posedge clk)` processes with one shared, known scalar input clock. Every process reads the same old signal and memory snapshot. Its nonblocking assignments stage updates; the backend commits them together, then settles the continuous-assignment graph. Source order within a process still determines overlapping NBA priority. Distinct processes must own distinct destination words or memories. Even disjoint slices of the same word are rejected across processes.

The generated C++ executes these processes serially. Ninja build jobs and the recorded worker arguments do not establish parallel simulation speedup. Independent clocks, negedge, asynchronous reset, blocking procedural assignments, general `always_comb`, delays and event regions remain future work.

## Elaboration and hierarchy

Surelog still parses and elaborates the original input files. The UHDM exporter now walks instantiated modules and generated scopes, collects declarations by object identity, and assigns unique flat storage names. Input port bridges and output port bridges join the global continuous-assignment graph. Clock ports must connect directly through input ports to the shared top clock.

Parameters and generate conditions are resolved by Surelog; the runtime does not reinterpret their source text. Each module's expression sizing uses its elaborated instance context. Internal signal metadata retains paths such as `g.u0.q`, enabling flat VPI name lookup of those signals. This does not add VPI module objects for every scope.

UHDM can fold literal expressions before export. Where exact, unambiguous source spans connect an elaborated constant to the original UHDM syntax, the exporter recovers that syntax to preserve Verilog expression sizing. Ambiguous matches fail. Missing original metadata leaves the serialized elaborated constant in use, so this recovery is conditional on the frontend metadata. A checked native 128-bit decoder handles sized binary, octal, hexadecimal and decimal constants without UHDM's 64-bit numeric conversion.

Connections currently require equal widths. Inputs may use supported expressions; outputs require whole declared destination words. Open ports, inout/ref ports, output selections, instance arrays, hierarchical cross-references, escaped identifiers, primitives, functions and tasks remain unsupported. Global validation rejects multiple drivers and continuous combinational cycles.

## Signed and wider values

Structural signals, memories, constants and expressions accept four-state words from 1 to 128 bits, using canonical packed bounds `[W-1:0]`. Signedness is preserved in normalized JSON and external metadata. Arithmetic follows expression signedness and assignment sizing, including mixed signed/unsigned operands, sign extension, signed comparisons, multiplication, division, remainder, arithmetic right shift, conditionals and `$signed`/`$unsigned` casts. Packed selects and concatenations produce unsigned expressions. Unsized nonnegative decimal literals through INT32_MAX normalize to signed 32-bit values; larger values require explicit sized literals.

The runtime uses unsigned 128-bit native arithmetic and separate value/X/Z masks. Signed division operates on unsigned magnitudes, avoiding native signed overflow for MIN/-1. Arithmetic with X/Z produces X as specified by the bounded operators; extension and shifts preserve an X or Z sign bit. Exact-case sizing considers the selector and all labels together.

Indices remain an explicitly unsigned subset. Signed index/base expressions are rejected, except nonnegative signed constants; use `$unsigned(index)` when unsigned addressing is intended. Known indexed-select bases remain restricted to 0..INT32_MAX. Widths above 128, arbitrary packed/unpacked dimensions, structs, unions, enums and two-state types are not implemented.

Fixed select bounds, indexed-select widths and replication counts require literal constants after original-expression recovery. Calculated expressions in those positions are rejected to avoid trusting frontend folding with different sizing.

This structural C++ runtime requires a GCC/Clang target supporting `unsigned __int128`, as used by Linux/WSL builds. The separate flat counter IR and its 64-bit C ABI are unchanged. Rebuild generated SDK hosts and VPI libraries together: the structural C++ word and signal layouts changed.

External binary and vector interfaces preserve all 128 bits. VPI signed metadata and decimal reads retain the sign. Decimal deposits remain unsigned syntax, checked against the 128-bit range; negative decimal deposits are rejected. C++ `uint64_t(port)` rejects X/Z and values that do not fit. Use `.bits()` or `.value()` for wider data.

## Verification

The new same-file differential fixtures compare generated standalone C++ and reusable SDK models with Icarus. They cover shared-clock pipelines, parameterized sibling instances, generated instances, signed mixed widths, signed memory history and 8/65/128-bit arithmetic, with known values and X/Z stimuli. Native checks exhaust signed eight-bit comparison/division/remainder and use independent string-position oracles across wide selection boundaries. External tests exercise four vector limbs, decimal limits, signed MIN/-1 formatting and checked integer conversion.

Builds use Ninja with four jobs. The final review record reports the actual regression, sanitizer and coverage results; this document describes the implemented contract rather than claiming full simulator conformance.
