# Concatenation targets in clocked assignments

VeloSim's structural backend accepts nonblocking assignments such as:

```systemverilog
always_ff @(posedge clk) begin
  {high, low} <= data;
  {status[0], payload[index +: 4]} <= replacement;
end
```

Surelog parses and elaborates the original source. UHDM supplies the concatenation and selection structure; VeloSim validates it, emits a single RHS temporary and distributes its bits into staged C++ state. This reuses the existing four-state selection/deposit runtime and reusable C++ model interface. It adds no source-text pattern matching or new Python helpers.

The accepted target is a possibly nested concatenation of direct writable packed words or supported bit, fixed descending part, and indexed `+:`/`-:` selections. Total target width must be 1–64 bits. Each leaf must name a distinct destination word. Repeated parent words are rejected even when their constant slices are disjoint. Replication, memory elements, computed leaves and selected parents that are themselves selections are outside this subset. Existing unsigned canonical `[W-1:0]` declarations, one known-clock posedge process and [index value limits](rtl-selected-writes.md) still apply.

For `{high, low}`, the left leaf receives the most significant portion and the right leaf the least significant portion. Nested groups preserve this order. The RHS expression is evaluated once using the **sum** of leaf widths as its assignment context, then zero-extended or truncated to that total width. Only afterward is it split. For example, `{q[7:4], r[3:0]} <= (bit_input << 5)` evaluates the shift at eight bits; evaluating it separately at four bits would incorrectly lose the result.

Every RHS reference and dynamic target index reads pre-edge state. Earlier NBAs in the same process stage updates without changing those reads. Later assignments merge into the staged parent words in source order. A whole-word assignment after a concatenation can overwrite its selected changes; reversing the order lets the concatenation update those bits.

An unknown target index ignores that leaf's selected write, while valid siblings still update. Partially or fully out-of-range leaves retain their full position in the RHS split; only valid destination positions are changed. Unsupported known indexed bases above `INT32_MAX` raise an error before staged sequential words commit. The reusable-model matrix checks stored state preservation and recovery after such a failure. Inputs remain host-controlled.

The [100-scenario ledger](concat-target-100-cycles.md) separates implementation, native string oracles, IR rejection boundaries and actual-source comparisons. Reproduction in Linux/WSL with the pinned frontend/exporter installed:

```sh
VELOSIM_REAL_FIFO=1 dotnet test tests/VeloSim.Tests -c Release -m:4 \
  --filter 'FullyQualifiedName~RtlConcat'
```

Surelog and Icarus receive the identical generated `design.sv` path. Tests independently specify the complete port interface, strictly compare full output traces and retain source/tool/build hashes under `artifacts/rtl-indexed-concat-*`. Standalone worker settings 1/4/8 qualify repeatability of serial execution. They do not establish parallel simulation. Reusable SDKs use CMake/Ninja and four build jobs; the final NativeAOT CLI exports the same hashed SDK inputs.

Independent AI reviews accompany each tenth scenario. They are not human reviews or a claim of full Verilog conformance. Final coverage and publication outcomes are recorded in the delivered validation report.

The focused qualification contains four ten-vector directed source fixtures and ten SDK width-pair fixtures with 64 vectors each. The matrix covers known and X/Z data, reset/enable holds, both indexed directions, unknown/OOB bases and input/temporary name collisions. Every SDK checks unchanged outputs after an unsupported-index failure and compares recovery with a fresh model on the corresponding valid path.
