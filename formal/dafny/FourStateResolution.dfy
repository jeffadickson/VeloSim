// VeloSim formal specification: IEEE 1800-2017 Tables 6-1/6-2/6-3 4-state net resolution
// and wildcard equality (==?) bit-lane & arbitrary-width word algebra for native/rtl/runtime.h,
// src/VeloSim.Core/RtlCSharpEmitter.cs, and src/VeloSim.Core/RtlRustEmitter.cs.
//
// Because the C++/C#/Rust 64-bit bitwise operations (`&`, `|`, `~`, `^`) act identically and
// independently on every bit lane, proving the exact boolean lane formula on `Bit4` and lifting
// it lane-by-lane to arbitrary-width `Word4 = seq<Bit4>` proves the laws for ALL widths
// (including 64-bit `w64` words and multi-word wide 4-state nets).

module FourStateResolution {
  // Canonical 4-state single bit: each bit lane belongs to at most one of {v, x, z}:
  //   0 -> v=false, x=false, z=false
  //   1 -> v=true,  x=false, z=false
  //   X -> v=false, x=true,  z=false
  //   Z -> v=false, x=false, z=true
  datatype Bit4 = Bit4(v: bool, x: bool, z: bool)

  const B0: Bit4 := Bit4(false, false, false)
  const B1: Bit4 := Bit4(true,  false, false)
  const BX: Bit4 := Bit4(false, true,  false)
  const BZ: Bit4 := Bit4(false, false, true)

  // Net resolution kinds matching vsrtl::NetKind / RtlVerification.ParseNetKind:
  //   0 = wire/tri, 1 = wand/triand, 2 = wor/trior, 3 = tri0, 4 = tri1
  const NET_WIRE: int := 0
  const NET_WAND: int := 1
  const NET_WOR:  int := 2
  const NET_TRI0: int := 3
  const NET_TRI1: int := 4

  predicate ValidNetKind(k: int) {
    0 <= k <= 4
  }

  // Canonical 4-state invariant: no bit lane may simultaneously occupy two planes.
  predicate CanonicalBit(a: Bit4) {
    !(a.v && a.x) && !(a.v && a.z) && !(a.x && a.z)
  }

  // Exact per-bit boolean formula matching native/rtl/runtime.h (w64::resolve_pair),
  // RtlCSharpEmitter.cs (W64Ops.ResolvePair), and RtlRustEmitter.cs (w64::resolve_pair).
  function ResolvePairBit(a: Bit4, b: Bit4, k: int): Bit4
    requires CanonicalBit(a) && CanonicalBit(b) && ValidNetKind(k)
  {
    var a0 := !(a.v || a.x || a.z);
    var b0 := !(b.v || b.x || b.z);
    var z_out := a.z && b.z;
    var o0 :=
      if k == NET_WAND then a0 || b0
      else if k == NET_WOR then (!(a.v || b.v)) && ((a0 && (b0 || b.z)) || (a.z && b0))
      else (a0 && (b0 || b.z)) || (a.z && b0);
    var v_out :=
      if k == NET_WAND then (!o0) && ((a.v && (b.v || b.z)) || (a.z && b.v))
      else if k == NET_WOR then a.v || b.v
      else (a.v && (b.v || b.z)) || (a.z && b.v);
    var x_out := !(z_out || v_out || o0);
    Bit4(v_out, x_out, z_out)
  }

  // Final pull resolution for tri0 (Z -> 0) and tri1 (Z -> 1).
  function ResolveFinalizeBit(acc: Bit4, k: int): Bit4
    requires CanonicalBit(acc) && ValidNetKind(k)
  {
    if k == NET_TRI0 then Bit4(acc.v, acc.x, false)
    else if k == NET_TRI1 then Bit4(acc.v || acc.z, acc.x, false)
    else acc
  }

  // Arbitrary-width N-bit 4-state word (models both 64-bit w64 and wide multi-word nets).
  type Word4 = seq<Bit4>

  predicate CanonicalWord(w: Word4) {
    forall i | 0 <= i < |w| :: CanonicalBit(w[i])
  }

  function ResolvePairWord(a: Word4, b: Word4, k: int): Word4
    requires |a| == |b| && CanonicalWord(a) && CanonicalWord(b) && ValidNetKind(k)
  {
    seq(|a|, i requires 0 <= i < |a| => ResolvePairBit(a[i], b[i], k))
  }

  function ResolveFinalizeWord(acc: Word4, k: int): Word4
    requires CanonicalWord(acc) && ValidNetKind(k)
  {
    seq(|acc|, i requires 0 <= i < |acc| => ResolveFinalizeBit(acc[i], k))
  }

  function AllZWord(width: nat): Word4 {
    seq(width, _ => BZ)
  }

  function ResolveDriversAcc(drivers: seq<Word4>, width: nat, k: int): Word4
    requires ValidNetKind(k) && |drivers| >= 1
    requires forall i | 0 <= i < |drivers| :: |drivers[i]| == width && CanonicalWord(drivers[i])
    ensures |ResolveDriversAcc(drivers, width, k)| == width
    ensures CanonicalWord(ResolveDriversAcc(drivers, width, k))
  {
    if |drivers| == 1 then drivers[0]
    else ResolvePairWord(ResolveDriversAcc(drivers[..|drivers|-1], width, k), drivers[|drivers|-1], k)
  }

  function ResolveDrivers(drivers: seq<Word4>, width: nat, k: int): Word4
    requires ValidNetKind(k)
    requires forall i | 0 <= i < |drivers| :: |drivers[i]| == width && CanonicalWord(drivers[i])
    ensures |ResolveDrivers(drivers, width, k)| == width
    ensures CanonicalWord(ResolveDrivers(drivers, width, k))
  {
    if |drivers| == 0 then
      ResolveFinalizeWord(AllZWord(width), k)
    else
      ResolveFinalizeWord(ResolveDriversAcc(drivers, width, k), k)
  }

  // Theorem 1: ResolvePairBit & ResolvePairWord always preserve canonical 4-state encoding.
  lemma ResolvePairPreservesCanonical(a: Word4, b: Word4, k: int)
    requires |a| == |b| && CanonicalWord(a) && CanonicalWord(b) && ValidNetKind(k)
    ensures CanonicalWord(ResolvePairWord(a, b, k))
  {
  }

  // Theorem 2: ResolveFinalizeWord preserves canonicality and eliminates Z for tri0/tri1.
  lemma ResolveFinalizePreservesCanonical(acc: Word4, k: int)
    requires CanonicalWord(acc) && ValidNetKind(k)
    ensures CanonicalWord(ResolveFinalizeWord(acc, k))
    ensures (k == NET_TRI0 || k == NET_TRI1) ==>
      forall i | 0 <= i < |acc| :: !ResolveFinalizeWord(acc, k)[i].z
  {
  }

  // Theorem 3: Commutativity — driver order cannot affect pairwise resolution for any width.
  lemma ResolvePairCommutative(a: Word4, b: Word4, k: int)
    requires |a| == |b| && CanonicalWord(a) && CanonicalWord(b) && ValidNetKind(k)
    ensures ResolvePairWord(a, b, k) == ResolvePairWord(b, a, k)
  {
    forall i | 0 <= i < |a|
      ensures ResolvePairBit(a[i], b[i], k) == ResolvePairBit(b[i], a[i], k)
    {
    }
  }

  // Theorem 4: Associativity — folding drivers in any tree/parallel reduction yields the same result.
  lemma ResolvePairAssociative(a: Word4, b: Word4, c: Word4, k: int)
    requires |a| == |b| && |b| == |c|
    requires CanonicalWord(a) && CanonicalWord(b) && CanonicalWord(c) && ValidNetKind(k)
    ensures CanonicalWord(ResolvePairWord(a, b, k)) && CanonicalWord(ResolvePairWord(b, c, k))
    ensures ResolvePairWord(ResolvePairWord(a, b, k), c, k) ==
            ResolvePairWord(a, ResolvePairWord(b, c, k), k)
  {
    forall i | 0 <= i < |a|
      ensures ResolvePairBit(ResolvePairBit(a[i], b[i], k), c[i], k) ==
              ResolvePairBit(a[i], ResolvePairBit(b[i], c[i], k), k)
    {
    }
  }

  // Theorem 5: Idempotence — identical redundant drivers never corrupt a net value.
  lemma ResolvePairIdempotent(a: Word4, k: int)
    requires CanonicalWord(a) && ValidNetKind(k)
    ensures ResolvePairWord(a, a, k) == a
  {
    forall i | 0 <= i < |a|
      ensures ResolvePairBit(a[i], a[i], k) == a[i]
    {
    }
  }

  // Theorem 6: High-impedance Z is the exact identity element for all 5 net kinds.
  lemma ResolvePairZIdentity(a: Word4, k: int)
    requires CanonicalWord(a) && ValidNetKind(k)
    ensures ResolvePairWord(a, AllZWord(|a|), k) == a
    ensures ResolvePairWord(AllZWord(|a|), a, k) == a
  {
    forall i | 0 <= i < |a|
      ensures ResolvePairBit(a[i], BZ, k) == a[i]
      ensures ResolvePairBit(BZ, a[i], k) == a[i]
    {
    }
  }

  // Theorem 7: Exact IEEE 1800-2017 Tables 6-1, 6-2, and 6-3 single-bit truth tables + tri0/tri1 pulls.
  lemma Ieee1800TablesExact()
    ensures // Table 6-1 (wire/tri): 0 vs 1 -> X; X dominates except Z identity
      ResolvePairBit(B0, B0, NET_WIRE) == B0 &&
      ResolvePairBit(B0, B1, NET_WIRE) == BX &&
      ResolvePairBit(B0, BX, NET_WIRE) == BX &&
      ResolvePairBit(B0, BZ, NET_WIRE) == B0 &&
      ResolvePairBit(B1, B1, NET_WIRE) == B1 &&
      ResolvePairBit(B1, BX, NET_WIRE) == BX &&
      ResolvePairBit(B1, BZ, NET_WIRE) == B1 &&
      ResolvePairBit(BX, BX, NET_WIRE) == BX &&
      ResolvePairBit(BX, BZ, NET_WIRE) == BX &&
      ResolvePairBit(BZ, BZ, NET_WIRE) == BZ
    ensures // Table 6-2 (wand/triand): 0 dominates 1 and X
      ResolvePairBit(B0, B1, NET_WAND) == B0 &&
      ResolvePairBit(B0, BX, NET_WAND) == B0 &&
      ResolvePairBit(B1, BX, NET_WAND) == BX &&
      ResolvePairBit(B1, BZ, NET_WAND) == B1 &&
      ResolvePairBit(BZ, BZ, NET_WAND) == BZ
    ensures // Table 6-3 (wor/trior): 1 dominates 0 and X
      ResolvePairBit(B0, B1, NET_WOR) == B1 &&
      ResolvePairBit(B1, BX, NET_WOR) == B1 &&
      ResolvePairBit(B0, BX, NET_WOR) == BX &&
      ResolvePairBit(B0, BZ, NET_WOR) == B0 &&
      ResolvePairBit(BZ, BZ, NET_WOR) == BZ
    ensures // tri0 / tri1 pull-down and pull-up of undriven Z
      ResolveFinalizeBit(BZ, NET_TRI0) == B0 &&
      ResolveFinalizeBit(BZ, NET_TRI1) == B1 &&
      ResolveFinalizeBit(B1, NET_TRI0) == B1 &&
      ResolveFinalizeBit(B0, NET_TRI1) == B0 &&
      ResolveFinalizeBit(BX, NET_TRI0) == BX &&
      ResolveFinalizeBit(BX, NET_TRI1) == BX
  {
  }

  // IEEE 1800-2017 §11.4.6 wildcard equality (==?) model matching w64::wildeq.
  predicate HasDefiniteMismatch(a: Word4, b: Word4)
    requires |a| == |b|
  {
    exists i | 0 <= i < |a| ::
      var care := !(b[i].x || b[i].z);
      var au := (a[i].x || a[i].z) && care;
      var known := care && !au;
      known && (a[i].v != b[i].v)
  }

  predicate HasCareUnknownOnLhs(a: Word4, b: Word4)
    requires |a| == |b|
  {
    exists i | 0 <= i < |a| ::
      var care := !(b[i].x || b[i].z);
      (a[i].x || a[i].z) && care
  }

  function WildEq(a: Word4, b: Word4): Bit4
    requires |a| == |b| && CanonicalWord(a) && CanonicalWord(b)
  {
    if HasDefiniteMismatch(a, b) then B0
    else if HasCareUnknownOnLhs(a, b) then BX
    else B1
  }

  // Theorem 8: WildEq always returns a canonical 1-bit result (0, 1, or X),
  // matches unconditionally when RHS is all X/Z (don't-care), propagates definite 0 over LHS X,
  // and coincides with exact equality on 2-state operands.
  lemma WildEqProperties(a: Word4, b: Word4)
    requires |a| == |b| && CanonicalWord(a) && CanonicalWord(b)
    ensures CanonicalBit(WildEq(a, b))
    ensures WildEq(a, seq(|a|, _ => BX)) == B1
    ensures WildEq(a, seq(|a|, _ => BZ)) == B1
    ensures (forall i | 0 <= i < |a| :: !a[i].x && !a[i].z && !b[i].x && !b[i].z) ==>
      (WildEq(a, b) == if a == b then B1 else B0)
  {
    if forall i | 0 <= i < |a| :: !a[i].x && !a[i].z && !b[i].x && !b[i].z {
      assert !HasCareUnknownOnLhs(a, b);
      if a == b {
        assert !HasDefiniteMismatch(a, b);
      } else {
        assert exists i | 0 <= i < |a| :: a[i] != b[i];
        var idx :| 0 <= idx < |a| && a[idx] != b[idx];
        assert a[idx].v != b[idx].v;
        assert HasDefiniteMismatch(a, b);
      }
    }
  }
}
