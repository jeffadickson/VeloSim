# Arithmetic correctness gate — strict vs experimental X handling

VeloSim's initial increment kernel used **possible-value / bitwise carry propagation**. That can preserve known result bits in the presence of X/Z inputs, but it is **not the standard SystemVerilog arithmetic behavior** for `+`: if an operand contains X or Z, the arithmetic result is unknown across its full expression width.

This is important: an optimistic carry calculation must not silently be treated as IEEE-compliant `count + 1'b1`.

## New native opcodes

- `increment` (1): historical experimental optimistic carry semantics, retained for existing tests.
- `reset_increment` (2): historical experimental reset/X merge.
- `sv_if_reset_increment` (3): historical procedural-if reset with optimistic arithmetic.
- `sv_increment` (4): strict X/Z arithmetic, returns all-X for any unknown operand.
- `sv_if_reset_increment_strict` (5): procedural-if reset with strict X/Z arithmetic.

The normalized UHDM counter lowerer now emits opcode 5. The old experimental opcodes remain available for compatibility, but **must not be used as standard SystemVerilog arithmetic**.

## Verified locally

- 1,280 exhaustive binary 8-bit increments across 1, 2, 4, 8, and 16 worker configurations passed.
- 300 randomized four-state cases passed the independent possible-value oracle for the *legacy experimental* increment operation.
- Strict native opcodes 4 and 5 were compiled and verified to return all-X for an 8-bit operand containing X.
- Four locally available mocked pipeline tests passed after updating the strict lowering path.

## Remaining

Run differential tests against a real Icarus/Verilator installation and Surelog-produced UHDM. The GitHub stress-test file has been committed but its entire suite has not yet been run in CI.
