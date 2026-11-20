# Explicit-time execution

Current source-bound results and measurement limits: [WP-01–05 progress report](../verification/wp01-05-progress-20261027.json).

This is an implementation slice of WP-02/WP-03/WP-04, not closure of all six work packages. The legacy `TimedEvents` class remains an opaque-ID transport. New `HdlScheduler` executes resumable process frames and owns signal, fixed-memory, suspension and pending-update state.

The kernel drains active work, then zero-delay inactive work, then a sampled NBA batch. Changes during that batch can reactivate waiting processes; active/inactive/NBA iteration continues to quiescence. Positive delays enqueue future ticks. Both RHS values and memory addresses of delayed assignments are sampled at the assignment, rather than evaluated when their updates commit. The C# compiler turns typed timed IR into numeric PCs and immutable C++ executor code.

Timed statement IR preserves available typed file/line/column metadata. Source annotations are validated diagnostic data and never authorize reading a file. Generated instructions retain a last-source prefix in their process frame; process-execution failures include that prefix while failed advances preserve committed state. Missing frontend coordinates are omitted.

Public advances use a value-copy transaction. Overflow, invalid sampled addresses, exhausted execution/event/frame budgets and executor failures preserve the prior kernel state. Executors must keep mutable simulation data inside that state; arbitrary host side effects cannot be rolled back. Source-order queueing provides a reproducible choice within the supported subset, not a claim that a racy design has a unique IEEE-required order.

## Frontend and time units

The pinned Surelog writer omits parsed time scales from serialized UHDM. The guarded `tools/surelog-tagfix` overlay now preserves the frontend's parsed `TimeInfo` on elaborated module instances and emits a provenance attribute. It uses parsed compiler metadata; it does not search HDL text for time units. The overlay must use GNU C++ with the pinned installed SDK: a Clang overlay failed actual frontend compatibility checks.

Timed lowering requires that provenance on every participating module. A missing scale is rejected rather than assumed to mean seconds. Host ticks use the minimum declared module precision, recorded as `time_precision` (decimal seconds exponent) in IR. Integer delays are converted with checked powers of ten. The pinned UHDM schema stores simple `#N` controls as exact `VpiDelay` strings; only that unsigned-decimal serialization is accepted when a typed constant relation is absent. Typed runtime integer delay expressions are sampled at suspension and scaled with checked unsigned arithmetic; unknown, negative and overflowing values fail transactionally. Exact decimal delay serialization is quantized using .NET Decimal at the declaring module precision before conversion to global ticks (ties away from zero). Time-suffixed and runtime-real delay forms remain outside this slice.

The lowerer selects explicit-time IR for delay/wait/fork/named-event designs or multiple initial blocks. Existing untimed edge/DAG models retain their execution contract.

## SDK hosts

Use the qualified overlay to elaborate original HDL, then the matching `velosim-uhdm-rtl` exporter and `rtl-export` tool. Timed SDKs include `hdl_scheduler.h`, advertise explicit-time execution in the manifest, and expose `eventsPending()` and `nextTimeSlot()` on their generated class. The manifest hashes the additional header. C++ word ABI remains 2; rebuild hosts with matching generated/runtime headers.

`timeInc(delta)` followed by `eval()` executes earlier pending HDL events before applying input deposits at the requested host tick. Input changes at a future tick therefore cannot leak backward into suspended reads. Positive time advances mark the base model dirty, including calls through a base Model reference; a zero advance preserves its current dirty state. Sample results only after `eval()`. Cycle-row stimulus has no explicit time specification and is rejected for timed IR; use an SDK host. [timed_counter.sv](../examples/timed_counter.sv) and [timed_main.cpp](../examples/timed_main.cpp) illustrate the host loop.

## Verification and open scope

`tests/native/hdl_scheduler_tests.cpp` checks region ordering, NBA reactivation, sampled delayed addresses, future-host isolation, wait rearming, four-state edges, rollback, budgets and overflow. `RtlTimedTests.cs` compares generated SDK traces with independently specified results and the original HDL run by Icarus; configured Verilator 5.052 comparisons exclude startup-X cases because that reference is two-state. Exact current run outcomes belong in the qualification report; earlier passing runs are not evidence for later source edits.

Remaining acceptance includes runtime-real and time-suffixed delay conversion, intra-assignment event controls, general selected writes and ignored invalid-address behavior, expanded local lifetimes, timed task calls and recursion, broader sensitivity forms, named scopes and delayed nonblocking named triggers from HDL. Bare nonblocking named occurrences use the NBA region. The pinned frontend checked a different token kind from the one emitted by its parser, silently classifying `->>` as `->`; the guarded CompileStmt overlay corrects that mismatch and supplies a required provenance marker. Memory-cell changes now rearm implicit combinational and wait sensitivities, and runtime repeat counts live in their process frames. Observed/reactive/postponed regions, interfaces/modports and general net resolution remain explicitly outside the kernel's current execution contract. No UVM, full IEEE conformance, Windows native qualification or speedup is claimed.



## Pinned-reference disagreements

The current same-source fixtures retain two distinct Verilator 5.052 discrepancies: decimal delays in a coarser-precision child execute at globally rounded times, and a nonblocking named occurrence is not delivered to a later waiter in the same initial process. Decimal expectations agree with Icarus and independently calculated module-precision rounding. Icarus rejects the nonblocking-trigger and break/continue fixtures; those limitations are retained explicitly, and the nonblocking-event trace is qualified against the stated NBA-region contract and directed native tests. Reference discrepancies are recorded as separate expected reference traces, never counted as matching VeloSim conformance. Test harness logs preserve unsupported-reference diagnostics.

## Implicit sensitivity and diagnostics (current worktree)

`always @*` uses a wait-first explicit-time process. Read operands and selected destination addresses contribute dependencies; a plain assignment destination does not. An empty sensitivity never executes. Host inputs deposited before initial process execution act like static testbench initialization and are not remembered as later events. `always_comb` keeps its required startup execution. Internal initial writes can trigger an already registered implicit process; initial-process ordering is not a race-freedom guarantee.

`RtlDesign.TimingDiagnostics` exposes conservative `VS-RACE-WW` (multiple writers) and `VS-RACE-RW` (blocking writer and another process reading) warnings. Fork children are separate process fragments. Analysis deliberately does not infer time or path exclusion, and may therefore warn about temporally separated activity. It stops at 256 fragments or 1024 hazards with `VS-RACE-LIMIT`. Exported SDKs include `timing-diagnostics.txt` in their hashed input manifest. These warnings are neither dynamic race detection nor proof of race freedom.

The guarded frontend overlay additionally preserves typed identifier delays in intra-assignment controls. Delay and RHS sampling remain separate from later commit, including selected masked writes and concatenated copy-out. Decimal literals retain module-precision quantization; general suffixed/runtime-real delays are still open. Generated instruction text is capped at 16 MiB as well as 20,000 instructions.

Automatic function/task local declaration initializers are evaluated after value inputs are bound, on each invocation. This does not add static lifetime, `ref` arguments, recursion or suspended task bodies. Qualification results and remaining limitations must be read alongside the source-bound verification reports; the work packages remain open.

Task `return;` now terminates its symbolic continuation; value-result outputs must still be assigned on every return path. A task return carrying a value remains rejected. This is a zero-time automatic-task feature, not suspension inside a task.

Automatic input defaults preserve positional and named omissions, including leading empty slots. Defaults are evaluated only when used in the declaration scope. Constant calls used in widths apply supported integral constant/parameter defaults through the pinned UHDM evaluator; broader constant-default shapes fail explicitly. Re-elaborate subroutine-containing modules with argument protocol v4, including models whose calls folded away. See [argument qualification](../verification/wp05-arguments-20261028.json) and [frontend compatibility](procedural-types-arrays.md#input-defaults-and-frontend-compatibility). This does not add output/inout/ref defaults, static storage, runtime function loops, recursion or timed task bodies. Tests that check declaration scope explicitly trigger each call; task-hidden sensitivity is a separate open semantic qualification.
