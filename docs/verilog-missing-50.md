# Fifty missing capabilities at the start of the 50-iteration batch

Baseline: commit `2636dcc`. This inventory concerns the structural RTL execution backend, not Surelog's parser. Surelog can parse substantially more than VeloSim can execute. An iteration may implement a feature, qualify its semantics, or harden a rejection; fifty iterations do not imply fifty features completed.

1. Logical OR `||`.
2. Logical inequality `!=`.
3. Case equality `===`.
4. Case inequality `!==`.
5. Unsigned less-than `<`.
6. Unsigned less-or-equal `<=` expressions.
7. Unsigned greater-than `>`.
8. Unsigned greater-or-equal `>=`.
9. Bitwise XNOR `~^` / `^~`.
10. Unary minus.
11. Logical left shift `<<`.
12. Logical right shift `>>`.
13. Arithmetic right shift `>>>` (unsigned operands first).
14. Reduction AND.
15. Reduction NAND.
16. Reduction OR.
17. Reduction NOR.
18. Reduction XOR.
19. Reduction XNOR.
20. Unary plus.
21. Multiplication.
22. Unsigned division.
23. Unsigned remainder.
24. Four-state conditional `?:`.
25. Packed bit-select reads.
26. Constant packed part-select reads.
27. Indexed packed part-select reads.
28. Packed bit/part-select assignment targets.
29. Replication concatenation.
30. Concatenations with more than two operands.
31. Signed declarations and sign extension.
32. Explicit casts and signed/unsigned system conversions.
33. Words wider than 64 bits.
34. Two-state declarations with correct initialization.
35. Nonzero unbased unsized fill literals.
36. Declaration initializers and initial blocks.
37. Multiple sequential processes sharing one clock.
38. Multiple independent clocks in the structural backend.
39. Negedge-triggered processes.
40. Asynchronous reset event lists.
41. `always_comb` / procedural combinational logic.
42. Blocking procedural assignment semantics.
43. Procedural loops and loop control.
44. Module hierarchy and port connections.
45. Generate scopes and generated instance arrays.
46. Functions and tasks.
47. Multidimensional unpacked arrays and arbitrary bounds.
48. Resolved nets, multiple drivers and drive strengths.
49. General delays/event scheduling and timed testbenches.
50. User-facing source-location diagnostics for unsupported constructs.

The iteration ledger records the scope actually completed and evidence. Remaining items stay explicit backlog; no general Verilog/SystemVerilog compliance claim is made.

## Batch disposition

Items 1–24 are implemented for unsigned four-state words of 1–64 bits in the existing single-posedge-process backend. Item 13 covers unsigned `>>>`; signed arithmetic shifts remain unsupported with signed types. Items 25–50 remain missing. The other 26 iterations qualify the implemented operators, strengthen evidence and rejection checks, run builds/reviews/coverage, and package native artifacts. This is 24 language additions and 50 development/qualification tasks, not 50 language additions.

The exporter conservatively rejects signed-valued operation trees, including constant-folded negative integer values. Unmaterialized parameter values are rejected rather than converted to zero. Integer parameter arithmetic may therefore be rejected even where a more complete signedness/context model could safely accept it. Materialized positive integer parameters compared with unsigned state continue to work. Rejection tests cover signed expressions and negative integer parameters; the actual RTL matrix covers the accepted unsigned subset.

## Same input files

Actual RTL differential tests pass the same design source path to Surelog and Icarus. Surelog produces UHDM; VeloSim lowers that model into structural IR and compiles native C++. Icarus compiles that design together with a separately generated stimulus/testbench. Both receive the same input vectors, and full output traces are compared. The FIFO additionally has an independent queue oracle. Generated operator fixtures are real source files given to both compilers; they are not hand-written IR substitutes. These are a supported SystemVerilog subset (`logic`/`always_ff`), not the full language.
