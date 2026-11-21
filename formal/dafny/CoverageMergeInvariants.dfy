// VeloSim formal specification: Fail-closed multi-run coverage merge algebra and
// IEEE 1800 §19.5.4/19.5.5 functional covergroup sampling (ignore_bins / illegal_bins / cross)
// for src/VeloSim.Core/RtlCoverageMerge.cs and native/rtl/vpi.cpp (VS-RTL-COVERAGE-001, VS-RTL-FUNC-COV-001).

module CoverageMergeInvariants {
  datatype ToggleEntry = ToggleEntry(signal: string, width: nat, rise: bv64, fall: bv64)

  datatype BinKind = Normal | Default | Ignore | Illegal

  datatype CoverBin = CoverBin(name: string, lo: nat, hi: nat, kind: BinKind, hits: nat)

  datatype PropertyStat = PropertyStat(name: string, kind: string, passes: nat, failures: nat, vacuous: nat)

  datatype CoverageReport = CoverageReport(
    model: string,
    pointHits: seq<nat>,
    toggles: seq<ToggleEntry>,
    cgNames: seq<string>,
    cgSamples: seq<nat>,
    propStats: seq<PropertyStat>
  )

  datatype MergeResult<T> = Ok(value: T) | Error

  predicate CompatibleReports(a: CoverageReport, b: CoverageReport) {
    a.model == b.model &&
    |a.pointHits| == |b.pointHits| &&
    |a.toggles| == |b.toggles| &&
    (forall i | 0 <= i < |a.toggles| ::
      a.toggles[i].signal == b.toggles[i].signal && a.toggles[i].width == b.toggles[i].width) &&
    a.cgNames == b.cgNames &&
    |a.cgSamples| == |a.cgNames| && |b.cgSamples| == |b.cgNames| &&
    |a.propStats| == |b.propStats| &&
    (forall i | 0 <= i < |a.propStats| ::
      a.propStats[i].name == b.propStats[i].name && a.propStats[i].kind == b.propStats[i].kind)
  }

  function MergeReports(a: CoverageReport, b: CoverageReport): MergeResult<CoverageReport> {
    if !CompatibleReports(a, b) then Error
    else Ok(CoverageReport(
      a.model,
      seq(|a.pointHits|, i requires 0 <= i < |a.pointHits| => a.pointHits[i] + b.pointHits[i]),
      seq(|a.toggles|, i requires 0 <= i < |a.toggles| =>
        ToggleEntry(a.toggles[i].signal, a.toggles[i].width, a.toggles[i].rise | b.toggles[i].rise, a.toggles[i].fall | b.toggles[i].fall)),
      a.cgNames,
      seq(|a.cgSamples|, i requires 0 <= i < |a.cgSamples| => a.cgSamples[i] + b.cgSamples[i]),
      seq(|a.propStats|, i requires 0 <= i < |a.propStats| =>
        PropertyStat(
          a.propStats[i].name,
          a.propStats[i].kind,
          a.propStats[i].passes + b.propStats[i].passes,
          a.propStats[i].failures + b.propStats[i].failures,
          a.propStats[i].vacuous + b.propStats[i].vacuous))
    ))
  }

  // Theorem 1: Fail-closed — incompatible models, toggle widths, covergroup sets, or property kinds always yield Error.
  lemma MergeIsFailClosed(a: CoverageReport, b: CoverageReport)
    requires !CompatibleReports(a, b)
    ensures MergeReports(a, b) == Error
  {
  }

  // Theorem 2: Commutativity — merging reports in either order yields the exact same result.
  lemma MergeIsCommutative(a: CoverageReport, b: CoverageReport)
    ensures MergeReports(a, b) == MergeReports(b, a)
  {
    if CompatibleReports(a, b) {
      assert CompatibleReports(b, a);
      var ab := MergeReports(a, b).value;
      var ba := MergeReports(b, a).value;
      assert ab.pointHits == ba.pointHits;
      assert forall i | 0 <= i < |ab.toggles| :: ab.toggles[i] == ba.toggles[i];
      assert ab.toggles == ba.toggles;
      assert ab.cgSamples == ba.cgSamples;
      assert forall i | 0 <= i < |ab.propStats| :: ab.propStats[i] == ba.propStats[i];
      assert ab.propStats == ba.propStats;
      assert ab == ba;
    } else {
      assert !CompatibleReports(b, a);
    }
  }

  // Theorem 3: Associativity — merging N runs in any grouping/tree order yields the exact same result.
  lemma MergeIsAssociative(a: CoverageReport, b: CoverageReport, c: CoverageReport)
    requires CompatibleReports(a, b) && CompatibleReports(b, c)
    ensures MergeReports(a, b).Ok? && MergeReports(b, c).Ok?
    ensures MergeReports(MergeReports(a, b).value, c) == MergeReports(a, MergeReports(b, c).value)
  {
    var ab := MergeReports(a, b).value;
    var bc := MergeReports(b, c).value;
    assert CompatibleReports(ab, c);
    assert CompatibleReports(a, bc);
    var lhs := MergeReports(ab, c).value;
    var rhs := MergeReports(a, bc).value;
    assert lhs.pointHits == rhs.pointHits;
    assert forall i | 0 <= i < |lhs.toggles| ::
      (a.toggles[i].rise | b.toggles[i].rise) | c.toggles[i].rise ==
      a.toggles[i].rise | (b.toggles[i].rise | c.toggles[i].rise);
    assert forall i | 0 <= i < |lhs.toggles| ::
      (a.toggles[i].fall | b.toggles[i].fall) | c.toggles[i].fall ==
      a.toggles[i].fall | (b.toggles[i].fall | c.toggles[i].fall);
    assert forall i | 0 <= i < |lhs.toggles| :: lhs.toggles[i] == rhs.toggles[i];
    assert lhs.toggles == rhs.toggles;
    assert lhs.cgSamples == rhs.cgSamples;
    assert forall i | 0 <= i < |lhs.propStats| :: lhs.propStats[i] == rhs.propStats[i];
    assert lhs.propStats == rhs.propStats;
    assert lhs == rhs;
  }

  // Theorem 4: Monotonicity — merged counters and toggle masks never lose hits or toggle bits.
  lemma MergeIsMonotonic(a: CoverageReport, b: CoverageReport)
    requires CompatibleReports(a, b)
    ensures var m := MergeReports(a, b).value;
      (forall i | 0 <= i < |a.pointHits| :: m.pointHits[i] >= a.pointHits[i] && m.pointHits[i] >= b.pointHits[i]) &&
      (forall i | 0 <= i < |a.toggles| :: (m.toggles[i].rise & a.toggles[i].rise) == a.toggles[i].rise && (m.toggles[i].fall & a.toggles[i].fall) == a.toggles[i].fall) &&
      (forall i | 0 <= i < |a.cgSamples| :: m.cgSamples[i] >= a.cgSamples[i] && m.cgSamples[i] >= b.cgSamples[i])
  {
  }

  // IEEE 1800 §19.5.4/19.5.5 coverpoint sampling (matching native/rtl/vpi.cpp Model::sampleCovergroup).
  datatype SampleOutcome = Sampled(bins: seq<CoverBin>, matchedIdx: int) | IllegalHit

  predicate HitsIllegal(bins: seq<CoverBin>, val: nat) {
    exists i | 0 <= i < |bins| :: bins[i].kind == Illegal && bins[i].lo <= val <= bins[i].hi
  }

  predicate HitsIgnore(bins: seq<CoverBin>, val: nat) {
    exists i | 0 <= i < |bins| :: bins[i].kind == Ignore && bins[i].lo <= val <= bins[i].hi
  }

  predicate HitsNormal(bins: seq<CoverBin>, val: nat) {
    exists i | 0 <= i < |bins| :: bins[i].kind == Normal && bins[i].lo <= val <= bins[i].hi
  }

  function SampleCoverpoint(bins: seq<CoverBin>, val: nat): SampleOutcome {
    if HitsIllegal(bins, val) then IllegalHit
    else if HitsIgnore(bins, val) then Sampled(bins, -1)
    else if HitsNormal(bins, val) then
      Sampled(
        seq(|bins|, i requires 0 <= i < |bins| =>
          if bins[i].kind == Normal && bins[i].lo <= val <= bins[i].hi
          then bins[i].(hits := bins[i].hits + 1)
          else bins[i]),
        0)
    else
      Sampled(
        seq(|bins|, i requires 0 <= i < |bins| =>
          if bins[i].kind == Default then bins[i].(hits := bins[i].hits + 1) else bins[i]),
        0)
  }

  // Theorem 5: IEEE 1800 §19.5.4/19.5.5 priority — illegal_bins always fail closed before any bin
  // mutation; ignore_bins never increment any coverpoint bin and return matchedIdx == -1 (excluded from cross).
  lemma CoverpointSamplingPriority(bins: seq<CoverBin>, val: nat)
    ensures HitsIllegal(bins, val) ==> SampleCoverpoint(bins, val) == IllegalHit
    ensures !HitsIllegal(bins, val) && HitsIgnore(bins, val) ==>
      SampleCoverpoint(bins, val) == Sampled(bins, -1)
    ensures !HitsIllegal(bins, val) ==>
      var res := SampleCoverpoint(bins, val);
      res.Sampled? &&
      forall i | 0 <= i < |bins| ::
        (bins[i].kind == Ignore || bins[i].kind == Illegal) ==> res.bins[i].hits == bins[i].hits
  {
  }
}
