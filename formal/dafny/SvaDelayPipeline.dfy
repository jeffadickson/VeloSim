// VeloSim formal specification: Bounded concurrent SVA property monitor shift-register
// semantics, disable_iff reset gating, and pass/fail/vacuous accounting for
// src/VeloSim.Core/RtlVerification.cs, RtlCSharpEmitter.cs, and RtlRustEmitter.cs (VS-RTL-SVA-001).

module SvaDelayPipeline {
  datatype PropKind = Assert | Cover | Assume

  datatype MonitorState = MonitorState(
    delay: nat,
    kind: PropKind,
    pipe: seq<bool>,
    passes: nat,
    failures: nat,
    vacuous: nat
  )

  predicate ValidMonitor(st: MonitorState) {
    st.delay <= 64 &&
    |st.pipe| == st.delay &&
    (st.kind == Cover ==> st.vacuous == 0)
  }

  function InitMonitor(delay: nat, kind: PropKind): MonitorState
    requires delay <= 64
    ensures ValidMonitor(InitMonitor(delay, kind))
  {
    MonitorState(delay, kind, seq(delay, _ => false), 0, 0, 0)
  }

  // Exact single clock-edge step matching EmitCppPropertyEvaluation / EmitCSharpModel / EmitRustModel.
  function StepMonitor(st: MonitorState, disableIff: bool, ant: bool, cons: bool): (MonitorState, bool)
    requires ValidMonitor(st)
  {
    if disableIff then
      (st.(pipe := seq(st.delay, _ => false)), false)
    else if st.delay == 0 then
      if ant then
        if cons then (st.(passes := st.passes + 1), false)
        else (st.(failures := st.failures + 1), st.kind == Assert)
      else
        (st.(vacuous := if st.kind == Cover then st.vacuous else st.vacuous + 1), false)
    else
      var due := st.pipe[st.delay - 1];
      var passes' := if due && cons then st.passes + 1 else st.passes;
      var failures' := if due && !cons then st.failures + 1 else st.failures;
      var failedAssert := due && !cons && st.kind == Assert;
      var pipe' := [ant] + st.pipe[..st.delay - 1];
      var vacuous' := if !ant && st.kind != Cover then st.vacuous + 1 else st.vacuous;
      (MonitorState(st.delay, st.kind, pipe', passes', failures', vacuous'), failedAssert)
  }

  // Theorem 1: StepMonitor always preserves ValidMonitor (including Cover => vacuous == 0).
  lemma StepPreservesValid(st: MonitorState, disableIff: bool, ant: bool, cons: bool)
    requires ValidMonitor(st)
    ensures ValidMonitor(StepMonitor(st, disableIff, ant, cons).0)
  {
  }

  // Theorem 2: disable_iff clears all pending obligations and never reports an assertion failure.
  lemma DisableIffClearsAllPending(st: MonitorState, ant: bool, cons: bool)
    requires ValidMonitor(st)
    ensures var (st', failed) := StepMonitor(st, true, ant, cons);
      !failed &&
      st'.passes == st.passes &&
      st'.failures == st.failures &&
      st'.vacuous == st.vacuous &&
      forall k | 0 <= k < st'.delay :: !st'.pipe[k]
  {
  }

  // Exact ground-truth predicate for whether an obligation initiated `k + 1` cycles ago
  // (0 <= k < delay) is still live in `pipe[k]` after running traces `dis`, `ants`, `conss` of length `t`.
  predicate ExpectedPipeSlot(dis: seq<bool>, ants: seq<bool>, t: nat, k: nat)
    requires t <= |dis| && t <= |ants|
  {
    t >= k + 1 &&
    ants[t - 1 - k] &&
    (forall j | t - 1 - k <= j < t :: !dis[j])
  }

  // Run the monitor for `t` clock cycles over traces `dis`, `ants`, `conss`.
  function RunMonitor(delay: nat, kind: PropKind, dis: seq<bool>, ants: seq<bool>, conss: seq<bool>, t: nat): MonitorState
    requires delay <= 64 && t <= |dis| && t <= |ants| && t <= |conss|
    ensures ValidMonitor(RunMonitor(delay, kind, dis, ants, conss, t))
    ensures RunMonitor(delay, kind, dis, ants, conss, t).delay == delay
  {
    if t == 0 then InitMonitor(delay, kind)
    else StepMonitor(RunMonitor(delay, kind, dis, ants, conss, t - 1), dis[t - 1], ants[t - 1], conss[t - 1]).0
  }

  // Theorem 3: At EVERY cycle `t` and EVERY pipeline slot `k` (`0 <= k < delay`),
  // `pipe[k]` equals `ExpectedPipeSlot(dis, ants, t, k)` — proving exact `|-> ##N` / `|=>` temporal
  // tracking across arbitrary sequences of disables and overlapping antecedents!
  lemma ShiftRegisterExactTemporalSemantics(
    delay: nat,
    kind: PropKind,
    dis: seq<bool>,
    ants: seq<bool>,
    conss: seq<bool>,
    t: nat
  )
    requires delay <= 64 && t <= |dis| && t <= |ants| && t <= |conss|
    ensures var st := RunMonitor(delay, kind, dis, ants, conss, t);
      forall k | 0 <= k < delay :: st.pipe[k] <==> ExpectedPipeSlot(dis, ants, t, k)
  {
    if t == 0 {
    } else {
      ShiftRegisterExactTemporalSemantics(delay, kind, dis, ants, conss, t - 1);
      var prev := RunMonitor(delay, kind, dis, ants, conss, t - 1);
      var cur := RunMonitor(delay, kind, dis, ants, conss, t);
      if dis[t - 1] {
        forall k | 0 <= k < delay
          ensures cur.pipe[k] <==> ExpectedPipeSlot(dis, ants, t, k)
        {
          if t >= k + 1 {
            assert t - 1 - k <= t - 1 < t;
            assert dis[t - 1];
          }
        }
      } else if delay > 0 {
        forall k | 0 <= k < delay
          ensures cur.pipe[k] <==> ExpectedPipeSlot(dis, ants, t, k)
        {
          if k == 0 {
            assert cur.pipe[0] == ants[t - 1];
            assert !dis[t - 1];
          } else {
            assert cur.pipe[k] == prev.pipe[k - 1];
            assert prev.pipe[k - 1] <==> ExpectedPipeSlot(dis, ants, t - 1, k - 1);
            assert !dis[t - 1];
          }
        }
      }
    }
  }

  // Theorem 4: Accounting conservation — on every non-disabled cycle for delay==0 assert/assume,
  // passes + failures + vacuous increases by exactly 1.
  lemma ZeroDelayAccountingConservation(st: MonitorState, ant: bool, cons: bool)
    requires ValidMonitor(st) && st.delay == 0 && st.kind != Cover
    ensures var st' := StepMonitor(st, false, ant, cons).0;
      st'.passes + st'.failures + st'.vacuous == st.passes + st.failures + st.vacuous + 1
  {
  }
}
