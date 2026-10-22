# Concatenation NBA targets: 100 scenarios

Baseline `19ae938`. This fresh batch numbers 100 bounded implementation and qualification scenarios, with ten local commits and an independent AI Verilog-focused review every ten. These are scenarios, not 100 new language features. Push only after final validation. No human peer review is claimed.

Scope: concatenations of direct writable unsigned packed words or existing bit/fixed/indexed packed targets, total width at most 64 bits. Nested concatenation groups are accepted. Each leaf must name a distinct parent word; even disjoint selections of the same parent are rejected in this initial subset. Memory leaves, replication targets, computed leaves and nested selected parents remain unsupported. Existing one-posedge-process and indexed-base limits apply.

The RHS is evaluated once in the total concatenation width, resized once, and split MSB-first by the declared width of each target leaf. RHS expressions and target indices read pre-edge state. Updates merge into staged next-state words and commit only after successful evaluation. Out-of-range/unknown selected leaves retain their RHS positions and do not suppress valid sibling writes.

| Checkpoint | Scenarios | Work | Validation / review |
|---|---|---|---|
|10|1–10|Target validation and emission|10/10 managed cases pass; exporter Ninja build passes; independent AI review accepted after fixture correction|
|20|11–20|Independent native split/deposit oracles|Ten width-pair scenarios and 6/6 native suites pass; independent review accepted|
|30|21–30|Actual whole-word assignments|Ten same-source vectors match Icarus at workers 1/4/8; 11 focused tests pass; independent review accepted|
|40|31–40|Invalid target and writer boundaries|21/21 focused tests pass; ten explicit rejection scenarios independently reviewed and accepted|
|50|41–50|Actual fixed/bit selection targets|Ten actual vectors pass at workers 1/4/8; 62 focused tests plus strengthened shift-5 rerun pass; reviewer sizing finding fixed and accepted|
|60|51–60|Dynamic target and sizing boundaries|30/30 managed IR cases pass; independent review accepted|
|70|61–70|Actual indexed targets and old indices|Planned|
|80|71–80|Nested groups and expression contexts|Planned|
|90|81–90|Actual nested/ordered assignments|Planned|
|100|91–100|Reusable C++ model matrix and documentation|Planned|

Final full-suite coverage, sanitizer, native publication and GitHub outcomes are recorded in the delivered validation report after the last source commit.
