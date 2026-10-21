# Fifty missing capabilities at the start of batch 2

Baseline: commit `295ad4a`. This inventory describes the structural RTL execution backend, not Surelog's parsing capability. The preceding batch added 24 unsigned operators; those implemented operators are not counted again here. Existing execution remains one posedge process, unsigned four-state words of at most 64 bits, restricted unpacked memories, ordered nonblocking assignments, and supported acyclic continuous expressions.

The following are exactly fifty distinct gaps at this baseline. Granular selection forms are listed separately because their read/write direction, bounds and assignment semantics differ. Related constructs are grouped where this batch does not plan to distinguish their implementations. Listing a gap does not promise its implementation in this batch.

1. Packed bit-select reads, including variable indices.
2. Constant packed part-select reads (`[msb:lsb]`).
3. Indexed ascending packed part-select reads (`[base +: width]`).
4. Indexed descending packed part-select reads (`[base -: width]`).
5. Packed bit-select nonblocking assignment targets.
6. Constant packed part-select nonblocking assignment targets.
7. Indexed ascending packed part-select nonblocking assignment targets.
8. Indexed descending packed part-select nonblocking assignment targets.
9. Concatenation expressions with more than two operands.
10. Constant replication concatenation expressions (`{count{expression}}`).
11. Concatenations as assignment targets.
12. Streaming concatenations.
13. Signed state and port declarations, including sign extension.
14. Signed arithmetic and relational expression semantics.
15. Sign-preserving arithmetic right shifts on signed operands.
16. Explicit casts and signed/unsigned conversion functions.
17. Packed words wider than 64 bits.
18. Two-state declarations with their initialization and coercion rules.
19. Nonzero unbased unsized fill literals (`'1`, `'x`, `'z`).
20. Declaration initializers.
21. Initial procedural blocks.
22. Multiple sequential processes sharing one clock.
23. Multiple independent sequential clocks in the structural backend.
24. Negedge-triggered sequential processes.
25. Asynchronous reset or other multi-event sensitivity lists.
26. Procedural combinational processes such as `always_comb`.
27. Blocking procedural assignments and their scheduling semantics.
28. Procedural `for` loops.
29. Procedural `while`, `repeat` and `do` loops.
30. Procedural `break` and `continue` control flow.
31. Module hierarchy and instance port connections.
32. Generate scopes and generated instance arrays.
33. User-defined functions.
34. User-defined tasks.
35. Multidimensional unpacked arrays.
36. Unpacked arrays with nonzero or descending bounds.
37. Dynamic arrays and their allocation operations.
38. Queue variables and queue operations.
39. Associative arrays and their indexing rules.
40. Array assignment patterns and aggregate array assignments.
41. Packed structure declarations and member access.
42. Packed union declarations and overlapping member access.
43. Enumeration declarations and typed enum operations.
44. Typed parameters beyond the exporter's restricted integer parameters.
45. Resolved nets with multiple drivers.
46. Drive strengths and pull-net behavior.
47. General timed/event scheduling beyond the synchronous batch contract.
48. Procedural `force` and `release`.
49. Assertion execution and assertion failure reporting.
50. Source-location diagnostics for unsupported structural constructs.

## Planned batch scope

The agreed implementation focus is four inventory items: 1, 2, 9 and 10 (packed bit-select reads, constant packed part-select reads, n-ary concatenation and constant replication). Indexed reads (items 3–4) and every selected assignment target (items 5–8) remain rejected and outside this batch. Every chosen item remains pending until actual evidence is recorded in the [batch 2 ledger](verilog-50-iterations-batch2.md). Explicit bounds, declaration ranges, unsigned sizing and four-state behavior must be qualified; unsupported forms must fail closed. The batch has fifty implementation/validation iterations, not a promise of fifty completed language capabilities.

Same-source comparisons must pass the identical generated design file to Surelog and Icarus, assert the design interface independently of exported IR, drive the same input vectors, and compare complete output traces. The native structural executable still executes one process serially; worker-setting comparisons do not establish parallel execution or speedup. Signed types, wider words and unrelated backlog items stay outside scope unless a later recorded scope change and evidence say otherwise.
