// Formal verification of VeloSim's pure .NET 11 Cap'n Proto + UHDM layer invariants:
//   1. Cap'n Proto packed-word tag/byte round-trip bijection (bv8 bitvector proof).
//   2. Cap'n Proto HoleSet struct-field alignment and disjointness.
//   3. Shared-DAG PackedTypeQuery termination and RAII budget unwinding.

module UhdmCapnpInvariants {
  // ---------------------------------------------------------------------------
  // 1. Cap'n Proto 8-byte word packing tag & byte-level round-trip bijection
  // ---------------------------------------------------------------------------
  type Byte = x: int | 0 <= x < 256
  type Word8 = w: seq<Byte> | |w| == 8 witness [0, 0, 0, 0, 0, 0, 0, 0]

  function MaskBit(i: nat): bv8
    requires i < 8
  {
    (1 as bv8) << i
  }

  function TagBit(tag: bv8, i: nat): bool
    requires i < 8
  {
    (tag & MaskBit(i)) != 0
  }

  function ComputeTag(w: Word8): bv8 {
    (if w[0] != 0 then 0x01 as bv8 else 0) |
    (if w[1] != 0 then 0x02 as bv8 else 0) |
    (if w[2] != 0 then 0x04 as bv8 else 0) |
    (if w[3] != 0 then 0x08 as bv8 else 0) |
    (if w[4] != 0 then 0x10 as bv8 else 0) |
    (if w[5] != 0 then 0x20 as bv8 else 0) |
    (if w[6] != 0 then 0x40 as bv8 else 0) |
    (if w[7] != 0 then 0x80 as bv8 else 0)
  }

  lemma ComputeTagBitMatches(w: Word8, i: nat)
    requires i < 8
    ensures TagBit(ComputeTag(w), i) <==> (w[i] != 0)
  {
  }

  function PackPayload(w: Word8, i: nat): seq<Byte>
    requires i <= 8
    decreases 8 - i
  {
    if i == 8 then []
    else if w[i] != 0 then [w[i]] + PackPayload(w, i + 1)
    else PackPayload(w, i + 1)
  }

  function UnpackWordRec(tag: bv8, payload: seq<Byte>, i: nat): seq<Byte>
    requires i <= 8
    decreases 8 - i
  {
    if i == 8 then []
    else if TagBit(tag, i) then
      if |payload| > 0 then [payload[0]] + UnpackWordRec(tag, payload[1..], i + 1)
      else [0] + UnpackWordRec(tag, [], i + 1)
    else
      [0] + UnpackWordRec(tag, payload, i + 1)
  }

  lemma PackUnpackWordRec(w: Word8, i: nat)
    requires i <= 8
    ensures UnpackWordRec(ComputeTag(w), PackPayload(w, i), i) == w[i..]
    decreases 8 - i
  {
    if i == 8 {
    } else {
      ComputeTagBitMatches(w, i);
      PackUnpackWordRec(w, i + 1);
    }
  }

  lemma CapnpWordPackUnpackRoundTrip(w: Word8)
    ensures UnpackWordRec(ComputeTag(w), PackPayload(w, 0), 0) == w
  {
    PackUnpackWordRec(w, 0);
  }

  // ---------------------------------------------------------------------------
  // 2. Cap'n Proto HoleSet Field Alignment & Disjointness
  // ---------------------------------------------------------------------------
  datatype BitRange = BitRange(start: nat, width: nat)

  predicate RangesDisjoint(a: BitRange, b: BitRange) {
    a.start + a.width <= b.start || b.start + b.width <= a.start
  }

  ghost predicate ValidField(f: BitRange, totalBits: nat) {
    f.width > 0 && (exists q: nat :: f.start == q * f.width) && f.start + f.width <= totalBits
  }

  ghost predicate AllDisjoint(fields: seq<BitRange>) {
    forall i, j :: 0 <= i < j < |fields| ==> RangesDisjoint(fields[i], fields[j])
  }

  // Splitting a power-of-two hole of size m = k*n (k >= 2) at base = q*m into a field of size n
  // plus a buddy remainder hole of size n at base + n preserves alignment and disjointness.
  lemma HoleSplitPreservesInvariants(q: nat, k: nat, n: nat, totalBits: nat)
    requires n > 0 && k >= 2
    requires q * (k * n) + k * n <= totalBits
    ensures ValidField(BitRange(q * (k * n), n), totalBits)
    ensures ValidField(BitRange(q * (k * n) + n, n), totalBits)
    ensures RangesDisjoint(BitRange(q * (k * n), n), BitRange(q * (k * n) + n, n))
  {
    var base := q * (k * n);
    assert base == (q * k) * n;
    assert base + n == (q * k + 1) * n;
  }

  // Appending a new 64-bit word yields an aligned field disjoint from all prior fields.
  lemma NewWordFieldPreservesDisjointness(prior: seq<BitRange>, dataWords: nat, width: nat)
    requires width == 1 || width == 16 || width == 32 || width == 64
    requires AllDisjoint(prior)
    requires forall i :: 0 <= i < |prior| ==> ValidField(prior[i], dataWords * 64)
    ensures ValidField(BitRange(dataWords * 64, width), (dataWords + 1) * 64)
    ensures AllDisjoint(prior + [BitRange(dataWords * 64, width)])
  {
    var f := BitRange(dataWords * 64, width);
    var mult: nat := if width == 1 then 64 else if width == 16 then 4 else if width == 32 then 2 else 1;
    assert dataWords * 64 == (dataWords * mult) * width;
    assert AllDisjoint(prior + [f]);
  }

  // ---------------------------------------------------------------------------
  // 3. Shared-DAG PackedTypeQuery Termination & Budget Unwinding
  // ---------------------------------------------------------------------------
  method PackedTypeQueryStep(active: nat, remaining: nat)
    returns (ok: bool, nextActive: nat, nextRemaining: nat)
    ensures ok ==> remaining > 0 && nextRemaining == remaining - 1 && nextActive == active
    ensures !ok ==> remaining == 0 && nextRemaining == 0 && nextActive == active
  {
    if remaining == 0 {
      return false, active, 0;
    }
    return true, active, remaining - 1;
  }

  method BoundedDagTraverse(maxSteps: nat, workUnits: nat)
    returns (completed: bool, stepsTaken: nat, finalActive: nat)
    requires maxSteps == 20000
    ensures stepsTaken <= maxSteps
    ensures finalActive == 0
    ensures completed <==> workUnits <= maxSteps
  {
    var active: nat := 1;
    var remaining: nat := maxSteps;
    var done: nat := 0;
    var ok := true;

    while done < workUnits && ok
      invariant 0 <= done <= workUnits
      invariant done <= maxSteps
      invariant remaining + done == maxSteps
      invariant active == 1
      invariant !ok ==> remaining == 0 && done == maxSteps && done < workUnits
      decreases workUnits - done, if ok then 1 else 0
    {
      var stepOk: bool;
      stepOk, active, remaining := PackedTypeQueryStep(active, remaining);
      if !stepOk {
        ok := false;
      } else {
        done := done + 1;
      }
    }

    // RAII Dispose always unwinds active count even on budget exhaustion.
    active := active - 1;
    return ok, done, active;
  }
}
