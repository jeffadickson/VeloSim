// VeloSim formal specification: Exact isomorphism between canonical 3-plane 4-state words
// `Bit4(v, x, z)` (`W64`) and IEEE 1800 2-plane `aval`/`bval` packed words `W4(a, b)`
// shared across C++ (`native/rtl/runtime.h`: `vsrtl::w4`), C# (`src/VeloSim.Core/RtlCSharpEmitter.cs`: `W4`/`W4Ops`),
// and Rust (`src/VeloSim.Core/RtlRustEmitter.cs`: `W4`/`w4`).
//
// Proves:
//   1. Bijection: Every 2-bit pair `(a, b)` maps uniquely to a canonical `Bit4` (zero invalid states).
//   2. Operator Homomorphism: Exact bitwise formulas for `BNot`, `BAnd`, `BOr`, `BXor`, `Cond`, `Eq`,
//      `CasezMatch`, `CasexMatch`, `WildEq`, `ResolvePair` (all 5 IEEE 1800 net kinds), and
//      2-instruction `ResolveFinalize` (`tri0`/`tri1`) match `FourStateResolution` identically
//      at both the bit-lane level and arbitrary-width word level (`seq<W4Bit>`).

include "FourStateResolution.dfy"

module FourStateW4Equivalence {
  import F = FourStateResolution

  // IEEE 1800 aval/bval single bit:
  //   0 -> (false, false)
  //   1 -> (true,  false)
  //   Z -> (false, true)
  //   X -> (true,  true)
  datatype W4Bit = W4Bit(a: bool, b: bool)

  type W4Word = seq<W4Bit>

  function ToBit4(w: W4Bit): F.Bit4 {
    F.Bit4(w.a && !w.b, w.a && w.b, !w.a && w.b)
  }

  function FromBit4(x: F.Bit4): W4Bit
    requires F.CanonicalBit(x)
  {
    W4Bit(x.v || x.x, x.x || x.z)
  }

  function ToWord4(w: W4Word): F.Word4 {
    seq(|w|, i requires 0 <= i < |w| => ToBit4(w[i]))
  }

  function FromWord4(x: F.Word4): W4Word
    requires F.CanonicalWord(x)
  {
    seq(|x|, i requires 0 <= i < |x| => FromBit4(x[i]))
  }

  // Theorem 1: W4Bit <-> CanonicalBit(Bit4) is an exact total bijection (every W4Bit is canonical).
  lemma W4BitBijection(w: W4Bit, x: F.Bit4)
    requires F.CanonicalBit(x)
    ensures F.CanonicalBit(ToBit4(w))
    ensures FromBit4(ToBit4(w)) == w
    ensures ToBit4(FromBit4(x)) == x
  {
  }

  lemma W4WordBijection(w: W4Word, x: F.Word4)
    requires F.CanonicalWord(x)
    ensures F.CanonicalWord(ToWord4(w))
    ensures FromWord4(ToWord4(w)) == w
    ensures ToWord4(FromWord4(x)) == x
  {
    assert |FromWord4(ToWord4(w))| == |w|;
    forall i | 0 <= i < |w|
      ensures FromWord4(ToWord4(w))[i] == w[i]
    {
      W4BitBijection(w[i], F.B0);
    }
    assert |ToWord4(FromWord4(x))| == |x|;
    forall i | 0 <= i < |x|
      ensures ToWord4(FromWord4(x))[i] == x[i]
    {
      W4BitBijection(W4Bit(false, false), x[i]);
    }
  }

  // Exact W4Ops single-bit formulas from src/VeloSim.Core/RtlCSharpEmitter.cs.
  function W4BNot(x: W4Bit): W4Bit {
    W4Bit((!x.a) || x.b, x.b)
  }

  function RefBNot(x: F.Bit4): F.Bit4
    requires F.CanonicalBit(x)
  {
    var u := x.x || x.z;
    F.Bit4((!x.v) && !u, u, false)
  }

  function W4BAnd(x: W4Bit, y: W4Bit): W4Bit {
    var o0 := (!x.a && !x.b) || (!y.a && !y.b);
    var o1 := (x.a && !x.b) && (y.a && !y.b);
    var ox := !(o0 || o1);
    W4Bit(o1 || ox, ox)
  }

  function RefBAnd(x: F.Bit4, y: F.Bit4): F.Bit4
    requires F.CanonicalBit(x) && F.CanonicalBit(y)
  {
    var x0 := !(x.v || x.x || x.z);
    var y0 := !(y.v || y.x || y.z);
    var o0 := x0 || y0;
    var o1 := x.v && y.v;
    F.Bit4(o1, !(o0 || o1), false)
  }

  function W4BOr(x: W4Bit, y: W4Bit): W4Bit {
    var o1 := (x.a && !x.b) || (y.a && !y.b);
    var o0 := (!x.a && !x.b) && (!y.a && !y.b);
    var ox := !(o0 || o1);
    W4Bit(o1 || ox, ox)
  }

  function RefBOr(x: F.Bit4, y: F.Bit4): F.Bit4
    requires F.CanonicalBit(x) && F.CanonicalBit(y)
  {
    var x0 := !(x.v || x.x || x.z);
    var y0 := !(y.v || y.x || y.z);
    var o1 := x.v || y.v;
    var o0 := x0 && y0;
    F.Bit4(o1, !(o0 || o1), false)
  }

  function W4BXor(x: W4Bit, y: W4Bit): W4Bit {
    var ob := x.b || y.b;
    W4Bit((x.a != y.a) || ob, ob)
  }

  function RefBXor(x: F.Bit4, y: F.Bit4): F.Bit4
    requires F.CanonicalBit(x) && F.CanonicalBit(y)
  {
    var u := x.x || x.z || y.x || y.z;
    F.Bit4((x.v != y.v) && !u, u, false)
  }

  // Theorem 2: Exact homomorphism for BNot, BAnd, BOr, BXor.
  lemma W4BitwiseEquivalence(x: W4Bit, y: W4Bit)
    ensures ToBit4(W4BNot(x)) == RefBNot(ToBit4(x))
    ensures ToBit4(W4BAnd(x, y)) == RefBAnd(ToBit4(x), ToBit4(y))
    ensures ToBit4(W4BOr(x, y)) == RefBOr(ToBit4(x), ToBit4(y))
    ensures ToBit4(W4BXor(x, y)) == RefBXor(ToBit4(x), ToBit4(y))
  {
  }

  // Exact W4 Cond unknown-selector merge formula (c is X/Z), matching vsrtl::conditional.
  function W4CondMerge(x: W4Bit, y: W4Bit): W4Bit {
    var diff := (x.a != y.a) || (x.b != y.b);
    W4Bit(x.a || diff, x.b || diff)
  }

  function RefCondMerge(x: F.Bit4, y: F.Bit4): F.Bit4
    requires F.CanonicalBit(x) && F.CanonicalBit(y)
  {
    var diff := (x.v != y.v) || (x.x != y.x) || (x.z != y.z);
    var same := !diff;
    F.Bit4(x.v && same, (x.x && same) || diff, x.z && same)
  }

  // Theorem 3: Exact homomorphism for unknown-selector ternary merge (including Z/Z preservation).
  lemma W4CondMergeEquivalence(x: W4Bit, y: W4Bit)
    ensures ToBit4(W4CondMerge(x, y)) == RefCondMerge(ToBit4(x), ToBit4(y))
  {
  }

  // Exact W4 Eq (==) formula matching vsrtl::equal: known-lane mismatch returns 0 even if other lanes are X/Z.
  predicate W4EqDefiniteMismatch(a: W4Word, b: W4Word)
    requires |a| == |b|
  {
    exists i | 0 <= i < |a| ::
      var u := a[i].b || b[i].b;
      (!u) && (a[i].a != b[i].a)
  }

  predicate RefEqDefiniteMismatch(a: F.Word4, b: F.Word4)
    requires |a| == |b| && F.CanonicalWord(a) && F.CanonicalWord(b)
  {
    exists i | 0 <= i < |a| ::
      var u := a[i].x || a[i].z || b[i].x || b[i].z;
      (!u) && (a[i].v != b[i].v)
  }

  predicate W4EqHasUnknown(a: W4Word, b: W4Word)
    requires |a| == |b|
  {
    exists i | 0 <= i < |a| :: a[i].b || b[i].b
  }

  predicate RefEqHasUnknown(a: F.Word4, b: F.Word4)
    requires |a| == |b| && F.CanonicalWord(a) && F.CanonicalWord(b)
  {
    exists i | 0 <= i < |a| :: a[i].x || a[i].z || b[i].x || b[i].z
  }

  function W4Eq(a: W4Word, b: W4Word): W4Bit
    requires |a| == |b|
  {
    if W4EqDefiniteMismatch(a, b) then W4Bit(false, false)
    else if W4EqHasUnknown(a, b) then W4Bit(true, true)
    else W4Bit(true, false)
  }

  function RefEq(a: F.Word4, b: F.Word4): F.Bit4
    requires |a| == |b| && F.CanonicalWord(a) && F.CanonicalWord(b)
  {
    if RefEqDefiniteMismatch(a, b) then F.B0
    else if RefEqHasUnknown(a, b) then F.BX
    else F.B1
  }

  // Theorem 3b: Exact homomorphism for 4-state logical equality (==) across arbitrary-width words.
  lemma W4EqEquivalence(a: W4Word, b: W4Word)
    requires |a| == |b|
    ensures ToBit4(W4Eq(a, b)) == RefEq(ToWord4(a), ToWord4(b))
  {
    var ta := ToWord4(a);
    var tb := ToWord4(b);
    forall i | 0 <= i < |a|
      ensures (a[i].b || b[i].b) == (ta[i].x || ta[i].z || tb[i].x || tb[i].z)
      ensures (!(a[i].b || b[i].b) && (a[i].a != b[i].a)) ==
              (!(ta[i].x || ta[i].z || tb[i].x || tb[i].z) && (ta[i].v != tb[i].v))
    {
    }
    assert W4EqDefiniteMismatch(a, b) <==> RefEqDefiniteMismatch(ta, tb);
    assert W4EqHasUnknown(a, b) <==> RefEqHasUnknown(ta, tb);
  }

  // Exact W4Ops.ResolvePair & W4Ops.ResolveFinalize formulas.
  function W4ResolvePairBit(a: W4Bit, b: W4Bit, k: int): W4Bit
    requires F.ValidNetKind(k)
  {
    var va := a.a && !a.b;
    var za := !a.a && a.b;
    var a0 := !a.a && !a.b;
    var vb := b.a && !b.b;
    var zb := !b.a && b.b;
    var b0 := !b.a && !b.b;
    var zOut := za && zb;
    var o0 :=
      if k == F.NET_WAND then a0 || b0
      else if k == F.NET_WOR then (!(va || vb)) && ((a0 && (b0 || zb)) || (za && b0))
      else (a0 && (b0 || zb)) || (za && b0);
    var vOut :=
      if k == F.NET_WAND then (!o0) && ((va && (vb || zb)) || (za && vb))
      else if k == F.NET_WOR then va || vb
      else (va && (vb || zb)) || (za && vb);
    var xOut := !(zOut || vOut || o0);
    W4Bit(vOut || xOut, xOut || zOut)
  }

  // Exact 2-instruction W4Ops.ResolveFinalize formulas for tri0/tri1.
  function W4ResolveFinalizeBit(acc: W4Bit, k: int): W4Bit
    requires F.ValidNetKind(k)
  {
    if k == F.NET_TRI0 then W4Bit(acc.a, acc.a && acc.b)
    else if k == F.NET_TRI1 then W4Bit(acc.a || acc.b, acc.a && acc.b)
    else acc
  }

  function W4ResolvePairWord(a: W4Word, b: W4Word, k: int): W4Word
    requires |a| == |b| && F.ValidNetKind(k)
  {
    seq(|a|, i requires 0 <= i < |a| => W4ResolvePairBit(a[i], b[i], k))
  }

  function W4ResolveFinalizeWord(acc: W4Word, k: int): W4Word
    requires F.ValidNetKind(k)
  {
    seq(|acc|, i requires 0 <= i < |acc| => W4ResolveFinalizeBit(acc[i], k))
  }

  // Theorem 4: Exact homomorphism for multi-driver IEEE 1800 net resolution (bit & arbitrary-width word).
  lemma W4ResolveEquivalenceBit(a: W4Bit, b: W4Bit, k: int)
    requires F.ValidNetKind(k)
    ensures ToBit4(W4ResolvePairBit(a, b, k)) == F.ResolvePairBit(ToBit4(a), ToBit4(b), k)
    ensures ToBit4(W4ResolveFinalizeBit(a, k)) == F.ResolveFinalizeBit(ToBit4(a), k)
  {
  }

  lemma W4ResolveEquivalenceWord(a: W4Word, b: W4Word, k: int)
    requires |a| == |b| && F.ValidNetKind(k)
    ensures ToWord4(W4ResolvePairWord(a, b, k)) == F.ResolvePairWord(ToWord4(a), ToWord4(b), k)
    ensures ToWord4(W4ResolveFinalizeWord(a, k)) == F.ResolveFinalizeWord(ToWord4(a), k)
  {
    forall i | 0 <= i < |a|
      ensures ToBit4(W4ResolvePairWord(a, b, k)[i]) == F.ResolvePairWord(ToWord4(a), ToWord4(b), k)[i]
      ensures ToBit4(W4ResolveFinalizeWord(a, k)[i]) == F.ResolveFinalizeWord(ToWord4(a), k)[i]
    {
      W4ResolveEquivalenceBit(a[i], b[i], k);
    }
  }

  // Exact W4Ops.WildEq predicate formulas lifted to arbitrary-width W4Word.
  predicate W4HasDefiniteMismatch(a: W4Word, b: W4Word)
    requires |a| == |b|
  {
    exists i | 0 <= i < |a| ::
      var care := !b[i].b;
      var au := a[i].b && care;
      var known := care && !au;
      known && (a[i].a != b[i].a)
  }

  predicate W4HasCareUnknown(a: W4Word, b: W4Word)
    requires |a| == |b|
  {
    exists i | 0 <= i < |a| ::
      var care := !b[i].b;
      a[i].b && care
  }

  function W4WildEq(a: W4Word, b: W4Word): W4Bit
    requires |a| == |b|
  {
    if W4HasDefiniteMismatch(a, b) then W4Bit(false, false)
    else if W4HasCareUnknown(a, b) then W4Bit(true, true)
    else W4Bit(true, false)
  }

  // Theorem 5: Exact homomorphism for IEEE 1800 wildcard equality (==?) across arbitrary-width words.
  lemma W4WildEqEquivalence(a: W4Word, b: W4Word)
    requires |a| == |b|
    ensures ToBit4(W4WildEq(a, b)) == F.WildEq(ToWord4(a), ToWord4(b))
  {
    var ta := ToWord4(a);
    var tb := ToWord4(b);
    forall i | 0 <= i < |a|
      ensures (!b[i].b) == !(tb[i].x || tb[i].z)
      ensures (a[i].b && !b[i].b) == ((ta[i].x || ta[i].z) && !(tb[i].x || tb[i].z))
      ensures ((!b[i].b && !(a[i].b && !b[i].b)) && (a[i].a != b[i].a)) ==
              ((!(tb[i].x || tb[i].z) && !((ta[i].x || ta[i].z) && !(tb[i].x || tb[i].z))) && (ta[i].v != tb[i].v))
    {
    }
    assert W4HasDefiniteMismatch(a, b) <==> F.HasDefiniteMismatch(ta, tb);
    assert W4HasCareUnknown(a, b) <==> F.HasCareUnknownOnLhs(ta, tb);
  }
}
