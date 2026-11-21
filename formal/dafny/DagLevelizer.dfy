// VeloSim formal specification: Structural combinational DAG levelization and
// data-race-free parallel level evaluation for src/VeloSim.Core/RtlCombinational.cs
// and native/rtl/thread_pool.h (VS-RTL-PARALLEL-001).

module DagLevelizer {
  // A combinational assignment graph has `n` steps (0..n-1), each writing a distinct
  // target signal `targets[i]` and reading dependency step indices `deps[i]` (subsets of 0..n-1).
  predicate ValidDagShape(n: nat, deps: seq<set<nat>>, targets: seq<nat>) {
    |deps| == n && |targets| == n &&
    (forall i | 0 <= i < n :: forall d | d in deps[i] :: d < n) &&
    (forall i, j | 0 <= i < n && 0 <= j < n && i != j :: targets[i] != targets[j])
  }

  // A topological level assignment `level` assigns each step a DAG depth strictly greater
  // than all of its dependencies.
  predicate ValidLevelization(n: nat, deps: seq<set<nat>>, level: seq<nat>) {
    |deps| == n && |level| == n &&
    (forall i | 0 <= i < n :: forall d | d in deps[i] :: d < n && level[d] < level[i])
  }

  // Theorem 1: Any two steps placed in the same level `L` have NO data dependency on each other
  // (no RAW, WAR, or WAW hazard), so they may execute concurrently in ThreadPool::parallel_for.
  lemma SameLevelHasNoHazards(n: nat, deps: seq<set<nat>>, targets: seq<nat>, level: seq<nat>, i: nat, j: nat)
    requires ValidDagShape(n, deps, targets)
    requires ValidLevelization(n, deps, level)
    requires i < n && j < n && i != j && level[i] == level[j]
    ensures !(j in deps[i]) && !(i in deps[j]) && targets[i] != targets[j]
  {
  }

  // Model state as a map from signal id -> value, and a pure step evaluation function.
  function ApplySteps(
    order: seq<nat>,
    targets: seq<nat>,
    eval: (nat, map<nat, int>) -> int,
    state: map<nat, int>
  ): map<nat, int>
    requires forall k | 0 <= k < |order| :: order[k] < |targets|
  {
    if |order| == 0 then state
    else
      var s := ApplySteps(order[..|order|-1], targets, eval, state);
      var idx := order[|order|-1];
      s[targets[idx] := eval(idx, s)]
  }

  // Step `idx` does not observe changes to signal `sig`.
  ghost predicate StepEvalIndependentOf(
    idx: nat,
    sig: nat,
    eval: (nat, map<nat, int>) -> int
  ) {
    forall s: map<nat, int>, v: int {:trigger eval(idx, s[sig := v])} ::
      eval(idx, s[sig := v]) == eval(idx, s)
  }

  // Theorem 2: Pairwise commutation — two distinct steps in the same level commute in any order.
  lemma SameLevelPairCommutes(
    n: nat,
    deps: seq<set<nat>>,
    targets: seq<nat>,
    level: seq<nat>,
    eval: (nat, map<nat, int>) -> int,
    s: map<nat, int>,
    a: nat,
    b: nat
  )
    requires ValidDagShape(n, deps, targets)
    requires ValidLevelization(n, deps, level)
    requires a < n && b < n && a != b && level[a] == level[b]
    requires StepEvalIndependentOf(a, targets[b], eval)
    requires StepEvalIndependentOf(b, targets[a], eval)
    ensures ApplySteps([a, b], targets, eval, s) == ApplySteps([b, a], targets, eval, s)
  {
    SameLevelHasNoHazards(n, deps, targets, level, a, b);
    assert [a, b][..1] == [a];
    assert [a][..0] == [];
    assert [b, a][..1] == [b];
    assert [b][..0] == [];
    var sa := s[targets[a] := eval(a, s)];
    var sb := s[targets[b] := eval(b, s)];
    assert ApplySteps([a], targets, eval, s) == sa;
    assert ApplySteps([b], targets, eval, s) == sb;
    assert eval(b, sa) == eval(b, s);
    assert eval(a, sb) == eval(a, s);
    var ab := ApplySteps([a, b], targets, eval, s);
    var ba := ApplySteps([b, a], targets, eval, s);
    assert ab == sa[targets[b] := eval(b, s)];
    assert ba == sb[targets[a] := eval(a, s)];
    assert ab == ba;
  }

  function RangeSet(n: nat): set<nat>
    ensures forall x: nat :: x in RangeSet(n) <==> x < n
    ensures |RangeSet(n)| == n
  {
    if n == 0 then {}
    else RangeSet(n - 1) + {n - 1}
  }

  lemma SubsetCard<T>(a: set<T>, b: set<T>)
    requires a <= b
    ensures |a| <= |b|
  {
    if a != {} {
      var x :| x in a;
      SubsetCard(a - {x}, b - {x});
    }
  }

  lemma StrictSubsetCard<T>(a: set<T>, b: set<T>, w: T)
    requires a <= b && w in b && !(w in a)
    ensures |a| < |b|
  {
    assert a <= b - {w};
    SubsetCard(a, b - {w});
  }

  method FindReadyNodes(n: nat, deps: seq<set<nat>>, placed: set<nat>) returns (ready: set<nat>)
    requires |deps| == n
    requires placed <= RangeSet(n)
    ensures ready <= RangeSet(n)
    ensures ready !! placed
    ensures forall i: nat | i in ready :: i < n && deps[i] <= placed
  {
    ready := {};
    var k: nat := 0;
    while k < n
      invariant 0 <= k <= n
      invariant ready <= RangeSet(k)
      invariant ready !! placed
      invariant forall i: nat | i in ready :: i < k && deps[i] <= placed
      decreases n - k
    {
      if !(k in placed) && deps[k] <= placed {
        ready := ready + {k};
      }
      k := k + 1;
    }
  }

  method AssignReadyLevel(n: nat, ready: set<nat>, lvlIn: seq<nat>, curLevel: nat) returns (lvlOut: seq<nat>)
    requires |lvlIn| == n
    requires ready <= RangeSet(n)
    ensures |lvlOut| == n
    ensures forall i: nat | i < n && i in ready :: lvlOut[i] == curLevel
    ensures forall i: nat | i < n && !(i in ready) :: lvlOut[i] == lvlIn[i]
  {
    lvlOut := seq(n, i requires 0 <= i < n => if i in ready then curLevel else lvlIn[i]);
  }

  // Theorem 3: Executable topological levelizer (matching RtlCombinational.cs Kahn-style
  // level assignment) terminates and either produces a valid levelization or fails closed on cycles.
  method ComputeLevels(n: nat, deps: seq<set<nat>>) returns (ok: bool, level: seq<nat>)
    requires |deps| == n
    requires forall i | 0 <= i < n :: forall d | d in deps[i] :: d < n
    ensures ok ==> ValidLevelization(n, deps, level)
  {
    var placed: set<nat> := {};
    var lvl: seq<nat> := seq(n, _ => 0);
    var curLevel: nat := 0;

    while |placed| < n
      invariant placed <= RangeSet(n)
      invariant |lvl| == n
      invariant forall i: nat | i in placed :: i < n && lvl[i] < curLevel
      invariant forall i: nat | i in placed ::
        forall d | d in deps[i] :: d < n && d in placed && lvl[d] < lvl[i]
      decreases n - |placed|
    {
      var ready := FindReadyNodes(n, deps, placed);
      if ready == {} {
        // Cycle detected: fail closed.
        return false, [];
      }

      var oldPlaced := placed;
      var newLvl := AssignReadyLevel(n, ready, lvl, curLevel);
      var newPlaced := oldPlaced + ready;

      var w :| w in ready;
      StrictSubsetCard(oldPlaced, newPlaced, w);
      SubsetCard(newPlaced, RangeSet(n));

      placed := newPlaced;
      lvl := newLvl;
      curLevel := curLevel + 1;
    }

    SubsetCard(placed, RangeSet(n));
    assert |placed| == n;
    assert forall i: nat | i < n :: i in placed by {
      forall i: nat | i < n ensures i in placed {
        if !(i in placed) {
          StrictSubsetCard(placed, RangeSet(n), i);
        }
      }
    }
    return true, lvl;
  }
}
