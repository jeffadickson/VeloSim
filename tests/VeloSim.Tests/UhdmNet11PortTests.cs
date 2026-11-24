using System.Text.Json;
using NUnit.Framework;
using VeloSim;
using VeloSim.Uhdm;

namespace VeloSim.Tests;

[TestFixture]
public sealed class UhdmNet11PortTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "velosim-uhdm-net11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // -------------------------------------------------------------------------
    // 1. Cap'n Proto packed wire codec + bounds/far-pointer tests
    // -------------------------------------------------------------------------

    [Test]
    public void CapnpPackedStream_RoundTrips_ZeroRuns_VerbatimRuns_And_PartialWords()
    {
        // Build an unpacked buffer containing:
        // - 5 zero words (tests 0x00 run compression)
        // - 3 dense words with all 8 bytes non-zero (tests 0xFF + verbatim run)
        // - 2 sparse words (tests general tag bitmask)
        // - 1 trailing zero word
        byte[] raw = new byte[11 * 8];
        // Words 5..7 dense
        for (int w = 5; w <= 7; w++)
            for (int b = 0; b < 8; b++)
                raw[w * 8 + b] = (byte)(0x10 + w * 8 + b);
        // Word 8 sparse: bytes 0, 3, 7 set
        raw[8 * 8 + 0] = 0xAA;
        raw[8 * 8 + 3] = 0xBB;
        raw[8 * 8 + 7] = 0xCC;
        // Word 9 sparse: byte 4 set
        raw[9 * 8 + 4] = 0x42;

        byte[] packed = CapnpPackedStream.Pack(raw);
        Assert.That(packed.Length, Is.LessThan(raw.Length));
        byte[] unpacked = CapnpPackedStream.Unpack(packed);
        Assert.That(unpacked, Is.EqualTo(raw));
    }

    [Test]
    public void CapnpPackedStream_And_Reader_RejectMalformedInputs()
    {
        Assert.Throws<InvalidDataException>(() => CapnpPackedStream.Unpack([]));
        Assert.Throws<InvalidDataException>(() => CapnpPackedStream.Unpack([0x00])); // truncated zero count
        Assert.Throws<InvalidDataException>(() => CapnpPackedStream.Unpack([0xFF, 1, 2, 3])); // truncated verbatim
        Assert.Throws<InvalidDataException>(() => CapnpPackedStream.Unpack([0xFF, 1, 2, 3, 4, 5, 6, 7, 8, 2, 9])); // truncated extra
        Assert.Throws<InvalidDataException>(() => CapnpPackedStream.Unpack([0x01])); // truncated partial word
        Assert.Throws<InvalidDataException>(() => CapnpPackedStream.Unpack([0x00, 10], maxUnpackedBytes: 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => CapnpPackedStream.Unpack([0x00, 0], maxUnpackedBytes: 0));
        Assert.Throws<ArgumentException>(() => CapnpPackedStream.Pack([1, 2, 3]));

        // Misaligned / truncated segment table
        Assert.Throws<InvalidDataException>(() => CapnpMessageReader.FromUnpacked([1, 2, 3, 4]));
        // Segment count > 4096
        byte[] badSegCount = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badSegCount.AsSpan(0, 4), 5000);
        Assert.Throws<InvalidDataException>(() => CapnpMessageReader.FromUnpacked(badSegCount));

        // Segment length out of bounds
        byte[] badSegLen = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badSegLen.AsSpan(4, 4), 99);
        Assert.Throws<InvalidDataException>(() => CapnpMessageReader.FromUnpacked(badSegLen));
    }

    [Test]
    public void CapnpMessageReader_ResolvesSingleAndDoubleFarPointers()
    {
        // Construct a 3-segment message:
        // Header: segCount-1 = 2 (3 segs), seg0=1 word, seg1=2 words, seg2=2 words => 4 uint32s = 16 bytes
        byte[] msg = new byte[16 + (1 + 2 + 2) * 8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(0, 4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(8, 4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(12, 4), 2);

        // Seg 0 word 0: double-far pointer -> seg 1 word 0 (kind=2, double=1, offset=0, targetSeg=1)
        ulong seg0Ptr = 2UL | (1UL << 2) | (1UL << 32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(16, 8), seg0Ptr);

        // Seg 1 word 0: far pointer to content in seg 2 word 0 (kind=2, double=0, offset=0, targetSeg=2)
        ulong pad0 = 2UL | (2UL << 32);
        // Seg 1 word 1: struct tag word (kind=0, offset=0, dataWords=1, ptrCount=0)
        ulong pad1 = (1UL << 32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(24, 8), pad0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(32, 8), pad1);

        // Seg 2 word 0: struct data payload = 0x1122334455667788
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(40, 8), 0x1122334455667788UL);

        var reader = CapnpMessageReader.FromUnpacked(msg);
        var root = reader.GetRootStruct();
        Assert.That(root.ReadUInt64(0), Is.EqualTo(0x1122334455667788UL));

        // Also test single-hop far pointer: seg 0 word 0 -> seg 1 word 0 (single-hop), seg 1 word 0 is struct ptr to seg 1 word 1
        ulong singleFar = 2UL | (1UL << 32);
        ulong structPtrInSeg1 = (1UL << 32); // offset=0 -> word 0+1+0 = word 1, dataWords=1, ptrCount=0
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(16, 8), singleFar);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(24, 8), structPtrInSeg1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(32, 8), 0xCAFEBABEDEADBEEFUL);
        var reader2 = CapnpMessageReader.FromUnpacked(msg);
        Assert.That(reader2.GetRootStruct().ReadUInt64(0), Is.EqualTo(0xCAFEBABEDEADBEEFUL));
    }

    [Test]
    public void CapnpMessageReader_HandlesMoreThanEightSegments()
    {
        // Construct a 10-segment message to exercise the overflow heap fallback beyond InlineSegmentInt8 (8 inline slots).
        // Header: segCount-1 = 9 (10 segs), 10 segment lengths => 11 uint32s = 44 bytes -> padded to 48 bytes (6 words).
        // Seg 9 has 2 words: word 0 = struct pointer to word 1, word 1 = data 0x0123456789ABCDEF.
        const int segCount = 10;
        byte[] msg = new byte[48 + (segCount + 1) * 8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(0, 4), segCount - 1);
        for (int s = 0; s < segCount; s++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4 + s * 4, 4), s == 9 ? 2u : 1u);

        ulong farToSeg9 = 2UL | (9UL << 32);
        ulong structPtrInSeg9 = (1UL << 32); // offset=0 -> word 1, dataWords=1, ptrCount=0
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(48, 8), farToSeg9);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(48 + 9 * 8, 8), structPtrInSeg9);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(48 + 10 * 8, 8), 0x0123456789ABCDEFUL);

        var reader = CapnpMessageReader.FromUnpacked(msg);
        Assert.That(reader.GetRootStruct().ReadUInt64(0), Is.EqualTo(0x0123456789ABCDEFUL));
    }

    // -------------------------------------------------------------------------
    // 2. Helper builders for constructing valid UHDM graphs in memory
    // -------------------------------------------------------------------------

    private static UhdmNode MakeLogicTypespec(UhdmDatabase db, int width, bool signed = false, bool twoState = false)
    {
        var ts = db.Create(twoState ? UhdmObjectType.BitTypespec : UhdmObjectType.LogicTypespec);
        if (signed) ts.SetBool("VpiSigned", true);
        if (width > 1)
        {
            var r = db.Create(UhdmObjectType.Range);
            r.SetRef("Left_expr", MakeConst(db, width - 1, 32));
            r.SetRef("Right_expr", MakeConst(db, 0, 32));
            ts.AddListRef("Ranges", r);
        }
        return ts;
    }

    private static UhdmNode MakeRefTypespec(UhdmDatabase db, UhdmNode target) =>
        db.Create(UhdmObjectType.RefTypespec).SetRef("Actual_typespec", target);

    private static UhdmNode MakeConst(UhdmDatabase db, long value, int width, string? vpiValue = null, string? decompile = null, bool signed = false, int constType = UhdmVpi.VpiDecConst)
    {
        var c = db.Create(UhdmObjectType.Constant)
            .SetInt("VpiSize", width)
            .SetInt("VpiConstType", signed ? UhdmVpi.VpiIntConst : constType)
            .SetString("VpiValue", vpiValue ?? $"INT:{value}");
        if (decompile is not null) c.SetString("VpiDecompile", decompile);
        return c;
    }

    private static UhdmNode MakeRef(UhdmDatabase db, string name, UhdmNode? actual = null, UhdmNode? parent = null)
    {
        var r = db.Create(UhdmObjectType.RefObj).SetString("VpiName", name);
        if (actual is not null) r.SetRef("Actual_group", actual);
        r.Parent = parent;
        return r;
    }

    private static UhdmNode MakeOp(UhdmDatabase db, int opType, params UhdmNode[] operands)
    {
        var op = db.Create(UhdmObjectType.Operation).SetInt("VpiOpType", opType);
        foreach (var o in operands) op.AddListRef("Operands", o);
        return op;
    }

    private static UhdmNode MakeLogicVar(UhdmDatabase db, UhdmNode parent, string name, int width, bool signed = false, bool twoState = false, string file = "test.sv", uint line = 1)
    {
        var ts = MakeLogicTypespec(db, width, signed, twoState);
        var v = db.Create(twoState ? UhdmObjectType.BitVar : UhdmObjectType.LogicVar)
            .SetString("VpiName", name)
            .SetRef("Typespec", MakeRefTypespec(db, ts));
        if (signed) v.SetBool("VpiSigned", true);
        v.Parent = parent;
        v.File = file;
        v.LineNo = line;
        return v;
    }

    private static UhdmNode MakePort(UhdmDatabase db, UhdmNode mod, string name, int direction, UhdmNode decl, UhdmNode? highConn = null)
    {
        var lowRef = MakeRef(db, name, decl, mod);
        var port = db.Create(UhdmObjectType.Port)
            .SetString("VpiName", name)
            .SetInt("VpiDirection", direction)
            .SetRef("Low_conn", lowRef);
        if (highConn is not null) port.SetRef("High_conn", highConn);
        port.Parent = mod;
        mod.AddListRef("Ports", port);
        return port;
    }

    private static UhdmDatabase BuildCounterUhdm(string topName = "slang_counter", int width = 8)
    {
        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", topName);
        var mod = db.Create(UhdmObjectType.ModuleInst)
            .SetString("VpiName", "work@" + topName)
            .SetString("VpiDefName", "work@" + topName);
        mod.Parent = design;
        mod.File = "counter.sv";
        mod.LineNo = 1;
        design.AddListRef("TopModules", mod);
        design.AddListRef("AllModules", mod);

        var clk = MakeLogicVar(db, mod, "clk", 1, file: "counter.sv", line: 2);
        var rstN = MakeLogicVar(db, mod, "rst_n", 1, file: "counter.sv", line: 3);
        var count = MakeLogicVar(db, mod, "count", width, file: "counter.sv", line: 4);
        mod.AddListRef("Variables", clk);
        mod.AddListRef("Variables", rstN);
        mod.AddListRef("Variables", count);
        MakePort(db, mod, "clk", UhdmVpi.VpiInput, clk);
        MakePort(db, mod, "rst_n", UhdmVpi.VpiInput, rstN);
        MakePort(db, mod, "count", UhdmVpi.VpiOutput, count);

        // always_ff @(posedge clk) if (!rst_n) count <= '0; else count <= count + 1'b1;
        var zeroAssign = db.Create(UhdmObjectType.Assignment)
            .SetBool("VpiBlocking", false)
            .SetRef("Lhs", MakeRef(db, "count", count, mod))
            .SetRef("Rhs", MakeConst(db, 0, width, $"BIN:{new string('0', width)}", "'0"));
        var incExpr = MakeOp(db, UhdmVpi.VpiAddOp,
            MakeRef(db, "count", count, mod),
            MakeConst(db, 1, 1, "BIN:1", "1'b1"));
        var incAssign = db.Create(UhdmObjectType.Assignment)
            .SetBool("VpiBlocking", false)
            .SetRef("Lhs", MakeRef(db, "count", count, mod))
            .SetRef("Rhs", incExpr);
        var ifElse = db.Create(UhdmObjectType.IfElse)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiNotOp, MakeRef(db, "rst_n", rstN, mod)))
            .SetRef("VpiStmt", zeroAssign)
            .SetRef("VpiElseStmt", incAssign);
        var begin = db.Create(UhdmObjectType.Begin);
        begin.AddListRef("Stmts", ifElse);
        var evCtrl = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiPosedgeOp, MakeRef(db, "clk", clk, mod)))
            .SetRef("Stmt", begin);
        var alwaysFf = db.Create(UhdmObjectType.Always)
            .SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysFF)
            .SetRef("Stmt", evCtrl);
        alwaysFf.Parent = mod;
        mod.AddListRef("Process", alwaysFf);

        return db;
    }

    // -------------------------------------------------------------------------
    // 3. Cap'n Proto UHDM binary round-trip + Inspector + Normalizer + Lowering
    // -------------------------------------------------------------------------

    [Test]
    public void UhdmDatabase_CapnpBinaryRoundTrip_Inspector_Normalizer_And_RtlLowerer_AllSucceed()
    {
        var original = BuildCounterUhdm("slang_counter", 8);
        byte[] packed = original.ToPackedBytes();
        var restored = UhdmDatabase.Load(packed);

        // Verify Inspector matches velosim.uhdm.inventory.v1
        string invJson = UhdmInspector.Inspect(restored);
        using (var invDoc = JsonDocument.Parse(invJson))
        {
            Assert.That(invDoc.RootElement.GetProperty("format").GetString(), Is.EqualTo("velosim.uhdm.inventory.v1"));
            var topMod = invDoc.RootElement.GetProperty("designs")[0].GetProperty("top_modules")[0];
            Assert.That(topMod.GetProperty("name").GetString(), Is.EqualTo("work@slang_counter"));
            Assert.That(topMod.GetProperty("objects").GetArrayLength(), Is.EqualTo(4)); // 3 vars + 1 process
        }

        // Verify Normalizer -> NormalizedUhdm.Lower -> IrDesign
        string normJson = UhdmNormalizer.Normalize(restored, "slang_counter");
        var irDesign = NormalizedUhdm.Lower(normJson);
        Assert.That(irDesign.Signals.Single(s => s.Name == "count").Initial.Width, Is.EqualTo(8));
        Assert.That(irDesign.Processes.Count, Is.EqualTo(1));

        // Verify RtlLowerer -> RtlDesign.Parse -> C# emit
        string rtlJson = UhdmRtlLowerer.LowerDatabase(restored, "slang_counter");
        var rtlDesign = RtlDesign.Parse(rtlJson);
        Assert.That(rtlDesign.Clock, Is.EqualTo("clk"));
        Assert.That(rtlDesign.Signals.Single(s => s.Name == "count").Width, Is.EqualTo(8));
        Assert.That(rtlDesign.EmitCpp(), Does.Contain("next[1]=arithmetic(s[1],lit(0x1ULL,1),8,false);"));
    }

    [Test]
    public void UhdmInspector_EnforcesDepthLimit_And_EmptyDesignRejection()
    {
        Assert.Throws<InvalidOperationException>(() => UhdmInspector.Inspect(new UhdmDatabase()));

        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", "deep");
        var root = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "m0");
        design.AddListRef("TopModules", root);
        var cur = root;
        for (int i = 1; i <= 130; i++)
        {
            var child = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "m" + i);
            cur.AddListRef("Modules", child);
            cur = child;
        }
        var ex = Assert.Throws<InvalidOperationException>(() => UhdmInspector.Inspect(db));
        Assert.That(ex!.Message, Does.Contain("128 levels"));
    }

    // -------------------------------------------------------------------------
    // 4. Exact port of tests/native/uhdm_audit_tests.cpp
    // -------------------------------------------------------------------------

    [Test]
    public void UhdmRtlAudit_MatchesNativeAuditTests_SharedGraph_Budgets_And_AllUnsupportedConstructs()
    {
        var db = new UhdmDatabase();
        var module = db.Create(UhdmObjectType.ModuleInst);
        module.File = "audit_fixture.sv";
        var variable = db.Create(UhdmObjectType.LogicVar);
        variable.Parent = module;
        module.AddListRef("Variables", variable);

        var valid = new UhdmRtlAudit();
        valid.Audit(module);
        Assert.That(valid.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(valid.Counts.ContainsKey(UhdmVpi.VpiModule), Is.True);

        // Shared declarations are visited once.
        int prevCount = valid.Count;
        valid.Audit(module);
        Assert.That(valid.Count, Is.EqualTo(prevCount));

        // Node budget rejection.
        var bounded = new UhdmRtlAudit(nodeBudget: 1);
        var ex = Assert.Throws<InvalidOperationException>(() => bounded.Audit(module));
        Assert.That(ex!.Message, Does.Contain("budget exceeded"));

        // Invalid budget arguments.
        foreach (var (nb, dbud) in new[] { (0, 1), (1, 0), (2_000_001, 1), (1, 257) })
            Assert.Throws<ArgumentException>(() => new UhdmRtlAudit(nb, dbud));

        // All 10 assertion/unsupported node types rejected with source coordinates.
        UhdmObjectType[] forbiddenTypes =
        [
            UhdmObjectType.AssertStmt, UhdmObjectType.ImmediateAssert, UhdmObjectType.ImmediateAssume,
            UhdmObjectType.ImmediateCover, UhdmObjectType.Assume, UhdmObjectType.Cover,
            UhdmObjectType.FinalStmt, UhdmObjectType.ClockingBlock, UhdmObjectType.Program,
            UhdmObjectType.InterfaceInst
        ];
        foreach (var kind in forbiddenTypes)
        {
            var m = db.Create(UhdmObjectType.ModuleInst);
            m.File = "audit_fixture.sv";
            var item = db.Create(kind);
            item.Parent = m;
            item.LineNo = 7;
            item.ColumnNo = 3;
            m.AddListRef("Assertions", item);
            var auditEx = Assert.Throws<InvalidOperationException>(() => new UhdmRtlAudit().Audit(m));
            Assert.That(auditEx!.Message, Does.Contain("unsupported executable UHDM construct").And.Contain("audit_fixture.sv:7:3"));
        }

        // Primitives and primitive arrays rejected too.
        var mp = db.Create(UhdmObjectType.ModuleInst);
        mp.File = "prim.sv";
        var gate = db.Create(UhdmObjectType.Gate);
        gate.Parent = mp;
        gate.LineNo = 12;
        gate.ColumnNo = 5;
        mp.AddListRef("Primitives", gate);
        var pEx = Assert.Throws<InvalidOperationException>(() => new UhdmRtlAudit().Audit(mp));
        Assert.That(pEx!.Message, Does.Contain("primitive instance").And.Contain("prim.sv:12:5"));
    }

    // -------------------------------------------------------------------------
    // 5. Exact port of tests/native/uhdm_type_budget_tests.cpp
    // -------------------------------------------------------------------------

    [Test]
    public void UhdmRtlTypes_MatchesNativeSharedUnionDagBudgetAndRecovery()
    {
        var db = new UhdmDatabase();
        var lowerer = new UhdmRtlLowerer(db) { UnionTagProvenance = true };
        var leaf = db.Create(UhdmObjectType.LogicTypespec);

        UhdmNode Layer(UhdmNode child)
        {
            var u = db.Create(UhdmObjectType.UnionTypespec).SetBool("VpiPacked", true);
            for (int i = 0; i < 8; i++)
            {
                var m = db.Create(UhdmObjectType.TypespecMember)
                    .SetString("VpiName", "m" + i)
                    .SetRef("Typespec", MakeRefTypespec(db, child));
                u.AddListRef("Members", m);
            }
            return u;
        }

        var small = Layer(leaf);
        var large = small;
        for (int i = 0; i < 7; i++) large = Layer(large);

        Assert.That(lowerer.AggregateWidth(small, 0), Is.EqualTo(1));
        Assert.That(lowerer.PackedQueryActive, Is.EqualTo(0));

        var exW = Assert.Throws<InvalidOperationException>(() => lowerer.AggregateWidth(large, 0));
        Assert.That(exW!.Message, Is.EqualTo("packed type traversal budget exceeded"));
        Assert.That(lowerer.PackedQueryActive, Is.EqualTo(0));

        // Recovery after budget failure.
        Assert.That(lowerer.AggregateWidth(small, 0), Is.EqualTo(1));
        Assert.That(lowerer.AggregateState(small, 0), Is.False);

        var exS = Assert.Throws<InvalidOperationException>(() => lowerer.AggregateState(large, 0));
        Assert.That(exS!.Message, Is.EqualTo("packed type traversal budget exceeded"));
        Assert.That(lowerer.PackedQueryActive, Is.EqualTo(0));
        Assert.That(lowerer.AggregateState(small, 0), Is.False);
    }

    // -------------------------------------------------------------------------
    // 6. Rich RTL Lowering: Hierarchy, Packed Struct/Union, Arrays, Containers,
    //    Operators, Case, Loops, System Functions/Tasks, Subroutines, Timed
    // -------------------------------------------------------------------------

    [Test]
    public void UhdmRtlLowerer_HandlesHierarchyFlattening_PackedStruct_Operators_Case_And_Loops()
    {
        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", "top");
        var marker = db.Create(UhdmObjectType.VoidTypespec).SetString("VpiName", "velosim frontend capability: union tags v1");
        design.AddListRef("Typespecs", marker);

        var top = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "top").SetString("VpiDefName", "top");
        top.Parent = design;
        top.File = "top.sv";
        top.LineNo = 1;
        design.AddListRef("TopModules", top);
        design.AddListRef("AllModules", top);

        var clk = MakeLogicVar(db, top, "clk", 1);
        var a = MakeLogicVar(db, top, "a", 8, signed: true);
        var b = MakeLogicVar(db, top, "b", 8, signed: true);
        var y = MakeLogicVar(db, top, "y", 8);
        var idx = MakeLogicVar(db, top, "idx", 4, twoState: true);
        top.AddListRef("Variables", clk);
        top.AddListRef("Variables", a);
        top.AddListRef("Variables", b);
        top.AddListRef("Variables", y);
        top.AddListRef("Variables", idx);
        MakePort(db, top, "clk", UhdmVpi.VpiInput, clk);
        MakePort(db, top, "a", UhdmVpi.VpiInput, a);
        MakePort(db, top, "b", UhdmVpi.VpiInput, b);
        MakePort(db, top, "y", UhdmVpi.VpiOutput, y);

        // Packed struct variable `pkt` with {hi: 4b, lo: 4b}
        var structTs = db.Create(UhdmObjectType.StructTypespec).SetBool("VpiPacked", true);
        var mHi = db.Create(UhdmObjectType.TypespecMember).SetString("VpiName", "hi").SetRef("Typespec", MakeRefTypespec(db, MakeLogicTypespec(db, 4)));
        var mLo = db.Create(UhdmObjectType.TypespecMember).SetString("VpiName", "lo").SetRef("Typespec", MakeRefTypespec(db, MakeLogicTypespec(db, 4)));
        structTs.AddListRef("Members", mHi);
        structTs.AddListRef("Members", mLo);
        var pkt = db.Create(UhdmObjectType.StructVar).SetString("VpiName", "pkt").SetRef("Typespec", MakeRefTypespec(db, structTs));
        pkt.Parent = top;
        top.AddListRef("Variables", pkt);

        // Child module instance `u_sub` with input clk_sub, input d, output q
        var sub = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "u_sub").SetString("VpiDefName", "sub");
        sub.Parent = top;
        var clkSub = MakeLogicVar(db, sub, "clk_sub", 1);
        var subD = MakeLogicVar(db, sub, "d", 8);
        var subQ = MakeLogicVar(db, sub, "q", 8);
        sub.AddListRef("Variables", clkSub);
        sub.AddListRef("Variables", subD);
        sub.AddListRef("Variables", subQ);
        MakePort(db, sub, "clk_sub", UhdmVpi.VpiInput, clkSub, MakeRef(db, "clk", clk, top));
        MakePort(db, sub, "d", UhdmVpi.VpiInput, subD, MakeRef(db, "a", a, top));
        MakePort(db, sub, "q", UhdmVpi.VpiOutput, subQ, MakeRef(db, "y", y, top));
        top.AddListRef("Modules", sub);

        // Child sequential process on posedge clk_sub (tests clock aliasing!)
        var subNba = db.Create(UhdmObjectType.Assignment)
            .SetBool("VpiBlocking", false)
            .SetRef("Lhs", MakeRef(db, "q", subQ, sub))
            .SetRef("Rhs", MakeRef(db, "d", subD, sub));
        var subEv = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiPosedgeOp, MakeRef(db, "clk_sub", clkSub, sub)))
            .SetRef("Stmt", subNba);
        var subProc = db.Create(UhdmObjectType.Always).SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysFF).SetRef("Stmt", subEv);
        sub.AddListRef("Process", subProc);

        // Top combinational process exercising HierPath struct write/read, casez, concat, replication, partselect, indexedselect, sysfuncs, loops
        var hpWrite = db.Create(UhdmObjectType.HierPath);
        hpWrite.AddListRef("Path_elems", MakeRef(db, "pkt", pkt, top));
        hpWrite.AddListRef("Path_elems", MakeRef(db, "hi", mHi, top));
        var hpRead = db.Create(UhdmObjectType.HierPath);
        hpRead.AddListRef("Path_elems", MakeRef(db, "pkt", pkt, top));
        hpRead.AddListRef("Path_elems", MakeRef(db, "lo", mLo, top));

        var partSel = db.Create(UhdmObjectType.PartSelect)
            .SetRef("Left_range", MakeConst(db, 3, 32))
            .SetRef("Right_range", MakeConst(db, 0, 32));
        partSel.Parent = a;

        var idxSel = db.Create(UhdmObjectType.IndexedPartSelect)
            .SetInt("VpiIndexedPartSelectType", UhdmVpi.VpiPosIndexed)
            .SetRef("Base_expr", MakeConst(db, 0, 32))
            .SetRef("Width_expr", MakeConst(db, 4, 32));
        idxSel.Parent = a;

        var seqBegin = db.Create(UhdmObjectType.Begin);
        seqBegin.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment)
            .SetBool("VpiBlocking", false)
            .SetRef("Lhs", hpWrite)
            .SetRef("Rhs", hpRead));
        var topSeqEv = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiPosedgeOp, MakeRef(db, "clk", clk, top)))
            .SetRef("Stmt", seqBegin);
        top.AddListRef("Process", db.Create(UhdmObjectType.Always).SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysFF).SetRef("Stmt", topSeqEv));

        var combBegin = db.Create(UhdmObjectType.Begin);

        // $bits, $clog2, $countones, $signed, inside, multiconcat
        var sysBits = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$bits");
        sysBits.AddListRef("Tf_call_args", MakeRef(db, "a", a, top));
        var sysClog2 = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$clog2");
        sysClog2.AddListRef("Tf_call_args", MakeConst(db, 16, 32));
        var sysCount = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$countones");
        sysCount.AddListRef("Tf_call_args", MakeRef(db, "a", a, top));
        var insideOp = MakeOp(db, UhdmVpi.VpiInsideOp, MakeRef(db, "a", a, top), MakeConst(db, 1, 8, "HEX:01"), MakeConst(db, 2, 8, "OCT:2"));
        var replOp = MakeOp(db, UhdmVpi.VpiMultiConcatOp, MakeConst(db, 2, 32), MakeConst(db, 5, 4, "BIN:0101"));
        var sumExpr = MakeOp(db, UhdmVpi.VpiAddOp,
            MakeOp(db, UhdmVpi.VpiBitXorOp, sysBits, sysClog2),
            MakeOp(db, UhdmVpi.VpiConditionOp, insideOp, sysCount, replOp));
        combBegin.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment)
            .SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "idx", idx, top))
            .SetRef("Rhs", sumExpr));

        // casez with multi-label item and default
        var caseStmt = db.Create(UhdmObjectType.CaseStmt)
            .SetInt("VpiCaseType", UhdmVpi.VpiCaseZ)
            .SetRef("VpiCondition", MakeRef(db, "idx", idx, top));
        var ci1 = db.Create(UhdmObjectType.CaseItem);
        ci1.AddListRef("VpiExprs", MakeConst(db, 0, 4, "BIN:0000"));
        ci1.AddListRef("VpiExprs", MakeConst(db, 1, 4, "BIN:0001"));
        ci1.SetRef("Stmt", db.Create(UhdmObjectType.Operation)
            .SetInt("VpiOpType", UhdmVpi.VpiPostIncOp)
            .AddListRef("Operands", MakeRef(db, "idx", idx, top)));
        var ciDef = db.Create(UhdmObjectType.CaseItem)
            .SetRef("Stmt", db.Create(UhdmObjectType.Begin));
        caseStmt.AddListRef("Case_items", ci1);
        caseStmt.AddListRef("Case_items", ciDef);
        combBegin.AddListRef("Stmts", caseStmt);

        // repeat(2) + for
        var rep = db.Create(UhdmObjectType.Repeat)
            .SetRef("VpiCondition", MakeConst(db, 2, 32))
            .SetRef("VpiStmt", db.Create(UhdmObjectType.Begin));
        combBegin.AddListRef("Stmts", rep);
        var forStmt = db.Create(UhdmObjectType.ForStmt)
            .SetRef("VpiForInitStmt", db.Create(UhdmObjectType.Assignment)
                .SetBool("VpiBlocking", true)
                .SetRef("Lhs", MakeRef(db, "idx", idx, top))
                .SetRef("Rhs", MakeConst(db, 0, 4, "BIN:0000")))
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiLtOp, MakeRef(db, "idx", idx, top), MakeConst(db, 4, 4, "BIN:0100")))
            .SetRef("VpiForIncStmt", MakeOp(db, UhdmVpi.VpiPostIncOp, MakeRef(db, "idx", idx, top)))
            .SetRef("VpiStmt", db.Create(UhdmObjectType.Begin));
        combBegin.AddListRef("Stmts", forStmt);

        var combProc = db.Create(UhdmObjectType.Always)
            .SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysComb)
            .SetRef("Stmt", combBegin);
        top.AddListRef("Process", combProc);

        // Round-trip through packed Cap'n Proto bytes first!
        var restored = UhdmDatabase.Load(db.ToPackedBytes());
        string rtlJson = UhdmRtlLowerer.LowerDatabase(restored, "top");

        var rtl = RtlDesign.Parse(rtlJson);
        Assert.That(rtl.Clock, Is.EqualTo("clk"));
        Assert.That(rtl.Signals.Any(s => s.Name == "pkt" && s.Width == 8), Is.True);
        Assert.That(rtlJson, Does.Contain("\"path\":\"u_sub.d\""));
        Assert.That(rtlJson, Does.Not.Contain("\"path\":\"u_sub.clk_sub\"")); // aliased to clk!
        Assert.That(rtl.Continuous.Count, Is.EqualTo(2)); // d <- a and y <- q
    }

    [Test]
    public void UhdmRtlLowerer_HandlesArrays_Dynamic_Queue_Associative_And_SysTasks()
    {
        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", "mem_top");
        var top = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "mem_top").SetString("VpiDefName", "mem_top");
        top.Parent = design;
        design.AddListRef("TopModules", top);
        design.AddListRef("AllModules", top);

        var clk = MakeLogicVar(db, top, "clk", 1);
        var key = MakeLogicVar(db, top, "k", 8, twoState: true);
        var val = MakeLogicVar(db, top, "v", 16, twoState: true);
        var ok = MakeLogicVar(db, top, "ok", 1, twoState: true);
        top.AddListRef("Variables", clk);
        top.AddListRef("Variables", key);
        top.AddListRef("Variables", val);
        top.AddListRef("Variables", ok);
        MakePort(db, top, "clk", UhdmVpi.VpiInput, clk);
        MakePort(db, top, "v", UhdmVpi.VpiOutput, val);

        // 1. Fixed 2D array [0:3][0:1]
        var arr2d = db.Create(UhdmObjectType.ArrayVar)
            .SetString("VpiName", "grid")
            .SetInt("VpiArrayType", UhdmVpi.VpiStaticArray);
        arr2d.AddListRef("Ranges", db.Create(UhdmObjectType.Range).SetRef("Left_expr", MakeConst(db, 0, 32)).SetRef("Right_expr", MakeConst(db, 3, 32)));
        arr2d.AddListRef("Ranges", db.Create(UhdmObjectType.Range).SetRef("Left_expr", MakeConst(db, 0, 32)).SetRef("Right_expr", MakeConst(db, 1, 32)));
        arr2d.AddListRef("Variables", MakeLogicVar(db, arr2d, "grid_elem", 16, twoState: true));
        top.AddListRef("Variables", arr2d);

        // 2. Dynamic array
        var dynArr = db.Create(UhdmObjectType.ArrayVar)
            .SetString("VpiName", "dyn")
            .SetInt("VpiArrayType", UhdmVpi.VpiDynamicArray);
        dynArr.AddListRef("Variables", MakeLogicVar(db, dynArr, "dyn_elem", 16, twoState: true));
        top.AddListRef("Variables", dynArr);

        // 3. Queue
        var queueArr = db.Create(UhdmObjectType.ArrayVar)
            .SetString("VpiName", "q")
            .SetInt("VpiArrayType", UhdmVpi.VpiQueueArray);
        queueArr.AddListRef("Variables", MakeLogicVar(db, queueArr, "q_elem", 16, twoState: true));
        top.AddListRef("Variables", queueArr);

        // 4. Associative array with 8-bit key
        var assocTs = db.Create(UhdmObjectType.ArrayTypespec)
            .SetRef("Index_typespec", MakeRefTypespec(db, MakeLogicTypespec(db, 8, twoState: true)));
        var assocArr = db.Create(UhdmObjectType.ArrayVar)
            .SetString("VpiName", "aa")
            .SetInt("VpiArrayType", UhdmVpi.VpiAssocArray)
            .SetRef("Typespec", MakeRefTypespec(db, assocTs));
        assocArr.AddListRef("Variables", MakeLogicVar(db, assocArr, "aa_elem", 16, twoState: true));
        top.AddListRef("Variables", assocArr);

        var body = db.Create(UhdmObjectType.Begin);
        // dyn = new[4];
        var newCall = db.Create(UhdmObjectType.MethodFuncCall).SetString("VpiName", "new");
        newCall.AddListRef("Tf_call_args", MakeConst(db, 4, 32));
        body.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "dyn", dynArr, top)).SetRef("Rhs", newCall));

        // q.push_back(v); v = q.pop_front(); q.delete();
        var pushCall = db.Create(UhdmObjectType.MethodTaskCall).SetString("VpiName", "push_back")
            .SetRef("Prefix", MakeRef(db, "q", queueArr, top));
        pushCall.AddListRef("Tf_call_args", MakeRef(db, "v", val, top));
        body.AddListRef("Stmts", pushCall);

        var popPath = db.Create(UhdmObjectType.HierPath);
        popPath.AddListRef("Path_elems", MakeRef(db, "q", queueArr, top));
        popPath.AddListRef("Path_elems", db.Create(UhdmObjectType.MethodFuncCall).SetString("VpiName", "pop_front"));
        body.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "v", val, top)).SetRef("Rhs", popPath));

        var delCall = db.Create(UhdmObjectType.MethodTaskCall).SetString("VpiName", "delete")
            .SetRef("Prefix", MakeRef(db, "q", queueArr, top));
        body.AddListRef("Stmts", delCall);

        // ok = aa.exists(k); ok = aa.first(k);
        var existsMethod = db.Create(UhdmObjectType.MethodFuncCall).SetString("VpiName", "exists");
        existsMethod.AddListRef("Tf_call_args", MakeRef(db, "k", key, top));
        var existsPath = db.Create(UhdmObjectType.HierPath);
        existsPath.AddListRef("Path_elems", MakeRef(db, "aa", assocArr, top));
        existsPath.AddListRef("Path_elems", existsMethod);
        body.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "ok", ok, top)).SetRef("Rhs", existsPath));

        var firstMethod = db.Create(UhdmObjectType.MethodFuncCall).SetString("VpiName", "first");
        firstMethod.AddListRef("Tf_call_args", MakeRef(db, "k", key, top));
        var firstPath = db.Create(UhdmObjectType.HierPath);
        firstPath.AddListRef("Path_elems", MakeRef(db, "aa", assocArr, top));
        firstPath.AddListRef("Path_elems", firstMethod);
        body.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "ok", ok, top)).SetRef("Rhs", firstPath));

        // $display("v=%0d", v); $finish;
        var disp = db.Create(UhdmObjectType.SysTaskCall).SetString("VpiName", "$display");
        disp.AddListRef("Tf_call_args", MakeConst(db, 0, 8, "STRING:v=%0d", "\"v=%0d\"", constType: UhdmVpi.VpiStringConst));
        disp.AddListRef("Tf_call_args", MakeRef(db, "v", val, top));
        body.AddListRef("Stmts", disp);
        body.AddListRef("Stmts", db.Create(UhdmObjectType.SysTaskCall).SetString("VpiName", "$finish"));

        var ev = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiPosedgeOp, MakeRef(db, "clk", clk, top)))
            .SetRef("Stmt", body);
        top.AddListRef("Process", db.Create(UhdmObjectType.Always).SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysFF).SetRef("Stmt", ev));

        var restored = UhdmDatabase.Load(db.ToPackedBytes());
        string rtlJson = UhdmRtlLowerer.LowerDatabase(restored, "mem_top");
        Assert.That(rtlJson, Does.Contain("\"dynamic\":true"));
        Assert.That(rtlJson, Does.Contain("\"queue\":true"));
        Assert.That(rtlJson, Does.Contain("\"associative\":true,\"key_width\":8"));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"allocate\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"queue_push\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"queue_pop\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"assoc_traverse\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"display\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"finish\""));
    }

    [Test]
    public void UhdmRtlLowerer_HandlesAutomaticFunction_And_Task_WithLoopUnrollingAndBranches()
    {
        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", "fn_top");
        var top = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "fn_top").SetString("VpiDefName", "fn_top");
        top.Parent = design;
        top.File = "fn.sv";
        top.LineNo = 1;
        top.AddListRef("Attributes", db.Create(UhdmObjectType.Attribute).SetString("VpiName", "velosim frontend argument slots v4"));
        design.AddListRef("TopModules", top);
        design.AddListRef("AllModules", top);

        var clk = MakeLogicVar(db, top, "clk", 1, file: "fn.sv", line: 2);
        var x = MakeLogicVar(db, top, "x", 8, file: "fn.sv", line: 3);
        var y = MakeLogicVar(db, top, "y", 8, file: "fn.sv", line: 4);
        top.AddListRef("Variables", clk);
        top.AddListRef("Variables", x);
        top.AddListRef("Variables", y);
        MakePort(db, top, "clk", UhdmVpi.VpiInput, clk);
        MakePort(db, top, "x", UhdmVpi.VpiInput, x);
        MakePort(db, top, "y", UhdmVpi.VpiOutput, y);

        // automatic function logic [7:0] add_loop(input logic [7:0] a, input logic [7:0] b = 8'd1);
        var fn = db.Create(UhdmObjectType.Function)
            .SetString("VpiName", "add_loop")
            .SetBool("VpiAutomatic", true);
        fn.Parent = top;
        fn.File = "fn.sv";
        fn.LineNo = 10;

        var retDecl = MakeLogicVar(db, fn, "add_loop", 8, file: "fn.sv", line: 10);
        fn.SetRef("Return", retDecl);

        var argA = MakeLogicVar(db, fn, "a", 8, file: "fn.sv", line: 11);
        var ioA = db.Create(UhdmObjectType.IoDecl)
            .SetString("VpiName", "a")
            .SetInt("VpiDirection", UhdmVpi.VpiInput)
            .SetRef("Expr", argA);
        var argB = MakeLogicVar(db, fn, "b", 8, file: "fn.sv", line: 12);
        argB.SetRef("Expr", MakeConst(db, 1, 8, "BIN:00000001"));
        var ioB = db.Create(UhdmObjectType.IoDecl)
            .SetString("VpiName", "b")
            .SetInt("VpiDirection", UhdmVpi.VpiInput)
            .SetRef("Expr", argB);
        fn.AddListRef("Io_decls", ioA);
        fn.AddListRef("Io_decls", ioB);

        var acc = MakeLogicVar(db, fn, "acc", 8, file: "fn.sv", line: 13);
        var iVar = db.Create(UhdmObjectType.IntVar).SetString("VpiName", "i")
            .SetRef("Typespec", MakeRefTypespec(db, db.Create(UhdmObjectType.IntTypespec).SetBool("VpiSigned", true)));
        iVar.Parent = fn;
        iVar.File = "fn.sv";
        iVar.LineNo = 14;
        fn.AddListRef("Variables", acc);
        fn.AddListRef("Variables", iVar);

        // acc = a; for (i = 0; i < 3; i++) acc = acc + b; if (a[0]) return acc; else return acc + 8'd2;
        var fnBody = db.Create(UhdmObjectType.Begin);
        fnBody.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "acc", acc, fn)).SetRef("Rhs", MakeRef(db, "a", argA, fn)));

        var loopStmt = db.Create(UhdmObjectType.ForStmt)
            .SetRef("VpiForInitStmt", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", false)
                .SetRef("Lhs", MakeRef(db, "i", iVar, fn)).SetRef("Rhs", MakeConst(db, 0, 32, "INT:0", signed: true)))
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiLtOp, MakeRef(db, "i", iVar, fn), MakeConst(db, 3, 32, "INT:3", signed: true)))
            .SetRef("VpiForIncStmt", MakeOp(db, UhdmVpi.VpiPostIncOp, MakeRef(db, "i", iVar, fn)))
            .SetRef("VpiStmt", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
                .SetRef("Lhs", MakeRef(db, "acc", acc, fn))
                .SetRef("Rhs", MakeOp(db, UhdmVpi.VpiAddOp, MakeRef(db, "acc", acc, fn), MakeRef(db, "b", argB, fn))));
        fnBody.AddListRef("Stmts", loopStmt);

        var retBranch = db.Create(UhdmObjectType.IfElse)
            .SetRef("VpiCondition", MakeRef(db, "a", argA, fn))
            .SetRef("VpiStmt", db.Create(UhdmObjectType.ReturnStmt).SetRef("VpiCondition", MakeRef(db, "acc", acc, fn)))
            .SetRef("VpiElseStmt", db.Create(UhdmObjectType.ReturnStmt)
                .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiAddOp, MakeRef(db, "acc", acc, fn), MakeConst(db, 2, 8, "BIN:00000010"))));
        fnBody.AddListRef("Stmts", retBranch);
        fn.SetRef("Stmt", fnBody);
        top.AddListRef("Task_funcs", fn);

        // Call add_loop(x, $velosim_omitted_actual_v4) to test default argument!
        var call = db.Create(UhdmObjectType.FuncCall).SetString("VpiName", "add_loop").SetRef("Function", fn);
        call.AddListRef("Tf_call_args", MakeRef(db, "x", x, top));
        call.AddListRef("Tf_call_args", MakeConst(db, 0, 32, "INT:0", "$velosim_omitted_actual_v4", constType: UhdmVpi.VpiIntConst));

        var ev = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiPosedgeOp, MakeRef(db, "clk", clk, top)))
            .SetRef("Stmt", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", false)
                .SetRef("Lhs", MakeRef(db, "y", y, top)).SetRef("Rhs", call));
        top.AddListRef("Process", db.Create(UhdmObjectType.Always).SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysFF).SetRef("Stmt", ev));

        var restored = UhdmDatabase.Load(db.ToPackedBytes());
        string rtlJson = UhdmRtlLowerer.LowerDatabase(restored, "fn_top");
        var rtl = RtlDesign.Parse(rtlJson);
        Assert.That(rtl.Clock, Is.EqualTo("clk"));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"cond\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"convert\""));
    }

    [Test]
    public void UhdmRtlLowerer_HandlesTimedProcesses_Delays_Fork_Wait_And_NamedEventTriggers()
    {
        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", "timed_top");
        var top = db.Create(UhdmObjectType.ModuleInst)
            .SetString("VpiName", "timed_top")
            .SetString("VpiDefName", "timed_top")
            .SetInt("VpiTimeUnit", -9)
            .SetInt("VpiTimePrecision", -12);
        top.Parent = design;
        top.File = "timed.sv";
        top.LineNo = 1;
        top.AddListRef("Attributes", db.Create(UhdmObjectType.Attribute).SetString("VpiName", "velosim frontend time scale v1"));
        top.AddListRef("Attributes", db.Create(UhdmObjectType.Attribute).SetString("VpiName", "velosim frontend named triggers v1"));
        design.AddListRef("TopModules", top);
        design.AddListRef("AllModules", top);

        var sig = MakeLogicVar(db, top, "sig", 8, file: "timed.sv", line: 2);
        top.AddListRef("Variables", sig);
        MakePort(db, top, "sig", UhdmVpi.VpiOutput, sig);

        var evDecl = db.Create(UhdmObjectType.NamedEvent).SetString("VpiName", "go");
        evDecl.Parent = top;
        top.AddListRef("Named_events", evDecl);

        // initial begin #5 sig = 8'd1; -> go; fork #2.5 sig = 8'd2; join_any wait_fork; end
        var initBody = db.Create(UhdmObjectType.Begin);
        initBody.File = "timed.sv";
        initBody.LineNo = 5;

        var d1 = db.Create(UhdmObjectType.DelayControl)
            .SetString("VpiDelay", "#5")
            .SetRef("Stmt", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
                .SetRef("Lhs", MakeRef(db, "sig", sig, top)).SetRef("Rhs", MakeConst(db, 1, 8, "BIN:00000001")));
        initBody.AddListRef("Stmts", d1);

        var trig = db.Create(UhdmObjectType.EventStmt).SetBool("VpiBlocking", true).SetRef("Named_event", evDecl);
        initBody.AddListRef("Stmts", trig);

        var fork = db.Create(UhdmObjectType.ForkStmt).SetInt("VpiJoinType", UhdmVpi.VpiJoinAny);
        var dReal = db.Create(UhdmObjectType.DelayControl)
            .SetString("VpiDelay", "#2.5")
            .SetRef("Stmt", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
                .SetRef("Lhs", MakeRef(db, "sig", sig, top)).SetRef("Rhs", MakeConst(db, 2, 8, "BIN:00000010")));
        fork.AddListRef("Stmts", dReal);
        initBody.AddListRef("Stmts", fork);
        initBody.AddListRef("Stmts", db.Create(UhdmObjectType.WaitFork));
        initBody.AddListRef("Stmts", db.Create(UhdmObjectType.DisableFork));

        top.AddListRef("Process", db.Create(UhdmObjectType.Initial).SetRef("Stmt", initBody));

        // Second timed process: always @(go) wait(sig) ;
        var waitStmt = db.Create(UhdmObjectType.WaitStmt)
            .SetRef("VpiCondition", MakeRef(db, "sig", sig, top))
            .SetRef("VpiStmt", db.Create(UhdmObjectType.NullStmt));
        var evWait = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeRef(db, "go", evDecl, top))
            .SetRef("Stmt", waitStmt);
        top.AddListRef("Process", db.Create(UhdmObjectType.Always).SetRef("Stmt", evWait));

        var restored = UhdmDatabase.Load(db.ToPackedBytes());
        string rtlJson = UhdmRtlLowerer.LowerDatabase(restored, "timed_top");
        Assert.That(rtlJson, Does.Contain("\"time_precision\":-12"));
        Assert.That(rtlJson, Does.Contain("\"named_events\":[\"go\"]"));
        Assert.That(rtlJson, Does.Contain("\"ticks\":5000"));
        Assert.That(rtlJson, Does.Contain("\"literal\":\"2.5\",\"unit\":-9,\"precision\":-12"));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"trigger\",\"name\":\"go\",\"nonblocking\":false"));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"fork\",\"join\":\"any\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"wait_fork\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"disable_fork\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"wait\""));
    }

    // -------------------------------------------------------------------------
    // 7. CLI commands & DesignPipeline builtin integration
    // -------------------------------------------------------------------------

    [Test]
    public async Task ToolCli_UhdmCommands_And_DesignPipeline_Builtin_EndToEnd()
    {
        string dir = TempDir();
        try
        {
            string uhdmPath = Path.Combine(dir, "counter.uhdm");
            BuildCounterUhdm("slang_counter", 8).Save(uhdmPath);

            // 1. uhdm-inspect (stdout and --output)
            using (var sw = new StringWriter())
            using (var err = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["uhdm-inspect", uhdmPath], sw, err);
                Assert.That(rc, Is.EqualTo(0), err.ToString());
                Assert.That(sw.ToString(), Does.Contain("velosim.uhdm.inventory.v1"));
            }

            string invOut = Path.Combine(dir, "inv.json");
            using (var sw = new StringWriter())
            using (var err = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["uhdm-inspect", uhdmPath, "--output", invOut], sw, err);
                Assert.That(rc, Is.EqualTo(0), err.ToString());
                Assert.That(File.Exists(invOut), Is.True);
            }

            // 2. uhdm-normalize
            string normOut = Path.Combine(dir, "norm.json");
            using (var sw = new StringWriter())
            using (var err = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["uhdm-normalize", uhdmPath, "--top", "slang_counter", "--output", normOut], sw, err);
                Assert.That(rc, Is.EqualTo(0), err.ToString());
                var ir = NormalizedUhdm.Lower(await File.ReadAllTextAsync(normOut));
                Assert.That(ir.Signals.Single(s => s.Name == "count").Initial.Width, Is.EqualTo(8));
            }

            // 3. uhdm-rtl (including --large-design)
            string rtlOut = Path.Combine(dir, "design.rtl.json");
            using (var sw = new StringWriter())
            using (var err = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["uhdm-rtl", uhdmPath, "--top", "slang_counter", "--output", rtlOut, "--large-design"], sw, err);
                Assert.That(rc, Is.EqualTo(0), err.ToString());
                var rtl = RtlDesign.Parse(await File.ReadAllTextAsync(rtlOut));
                Assert.That(rtl.Clock, Is.EqualTo("clk"));
            }

            // 4. DesignPipeline.CompileAsync & CompileRtlAsync using "builtin"
            string svPath = Path.Combine(dir, "slang_counter.sv");
            await File.WriteAllTextAsync(svPath, "module slang_counter(input logic clk, input logic rst_n, output logic [7:0] count); endmodule\n");
            var fakeSurelog = new FakeSurelogRunner(uhdmPath);
            var pipeline = new DesignPipeline(fakeSurelog);

            string irPath = await pipeline.CompileAsync([svPath], "slang_counter", Path.Combine(dir, "pipe-norm"), surelog: "surelog", normalizer: "builtin");
            Assert.That(File.Exists(irPath), Is.True);
            var compiledIr = IrDesign.Parse(await File.ReadAllTextAsync(irPath));
            Assert.That(compiledIr.Signals.Single(s => s.Name == "count").Initial.Width, Is.EqualTo(8));

            string compiledRtlPath = await pipeline.CompileRtlAsync([svPath], "slang_counter", Path.Combine(dir, "pipe-rtl"), surelog: "surelog", exporter: "builtin");
            Assert.That(File.Exists(compiledRtlPath), Is.True);
            var compiledRtl = RtlDesign.Parse(await File.ReadAllTextAsync(compiledRtlPath));
            Assert.That(compiledRtl.Clock, Is.EqualTo("clk"));

            // 5. CLI usage errors
            using (var sw = new StringWriter())
            using (var err = new StringWriter())
            {
                Assert.That(await ToolCli.RunAsync(["uhdm-inspect"], sw, err), Is.EqualTo(1));
                Assert.That(await ToolCli.RunAsync(["uhdm-rtl", uhdmPath], sw, err), Is.EqualTo(1));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public void UhdmRtlLowerer_HandlesTasks_NestedTaskCalls_Parameters_Enums_Selections_And_SysCalls()
    {
        var db = new UhdmDatabase();
        var design = db.Create(UhdmObjectType.Design).SetString("VpiName", "task_top");
        var top = db.Create(UhdmObjectType.ModuleInst).SetString("VpiName", "task_top").SetString("VpiDefName", "task_top");
        top.Parent = design;
        top.File = "task_top.sv";
        top.LineNo = 1;
        top.AddListRef("Attributes", db.Create(UhdmObjectType.Attribute).SetString("VpiName", "velosim frontend argument slots v4"));
        design.AddListRef("TopModules", top);
        design.AddListRef("AllModules", top);

        // Module parameter P = 8'd7
        var paramP = db.Create(UhdmObjectType.Parameter)
            .SetString("VpiName", "P")
            .SetRef("Typespec", MakeRefTypespec(db, MakeLogicTypespec(db, 8)));
        paramP.Parent = top;
        paramP.File = "task_top.sv";
        paramP.LineNo = 2;
        paramP.ColumnNo = 10;
        var paP = db.Create(UhdmObjectType.ParamAssign)
            .SetRef("Lhs", paramP)
            .SetRef("Rhs", MakeConst(db, 7, 8, "BIN:00000111"));
        paP.Parent = top;
        paP.File = "task_top.sv";
        paP.LineNo = 2;
        paP.ColumnNo = 10;
        top.AddListRef("Parameters", paramP);
        top.AddListRef("Param_assigns", paP);

        var clk = MakeLogicVar(db, top, "clk", 1, file: "task_top.sv", line: 3);
        var x = MakeLogicVar(db, top, "x", 8, file: "task_top.sv", line: 4);
        var y = MakeLogicVar(db, top, "y", 8, file: "task_top.sv", line: 5);
        var b = MakeLogicVar(db, top, "b", 1, file: "task_top.sv", line: 6);
        top.AddListRef("Variables", clk);
        top.AddListRef("Variables", x);
        top.AddListRef("Variables", y);
        top.AddListRef("Variables", b);
        MakePort(db, top, "clk", UhdmVpi.VpiInput, clk);
        MakePort(db, top, "x", UhdmVpi.VpiInput, x);
        MakePort(db, top, "y", UhdmVpi.VpiOutput, y);

        // Inner automatic task helper(inout logic [7:0] v, input logic [7:0] d)
        var helper = db.Create(UhdmObjectType.Task).SetString("VpiName", "helper").SetBool("VpiAutomatic", true);
        helper.Parent = top;
        helper.File = "task_top.sv";
        helper.LineNo = 10;
        var hV = MakeLogicVar(db, helper, "v", 8, file: "task_top.sv", line: 10);
        var hD = MakeLogicVar(db, helper, "d", 8, file: "task_top.sv", line: 10);
        helper.AddListRef("Io_decls", db.Create(UhdmObjectType.IoDecl).SetString("VpiName", "v").SetInt("VpiDirection", UhdmVpi.VpiInout).SetRef("Expr", hV));
        helper.AddListRef("Io_decls", db.Create(UhdmObjectType.IoDecl).SetString("VpiName", "d").SetInt("VpiDirection", UhdmVpi.VpiInput).SetRef("Expr", hD));
        var pRef = MakeRef(db, "P", paramP, helper);
        pRef.File = "task_top.sv"; pRef.LineNo = 11; pRef.ColumnNo = 5;
        helper.SetRef("Stmt", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "v", hV, helper))
            .SetRef("Rhs", MakeOp(db, UhdmVpi.VpiAddOp, MakeOp(db, UhdmVpi.VpiAddOp, MakeRef(db, "v", hV, helper), MakeRef(db, "d", hD, helper)), pRef)));
        top.AddListRef("Task_funcs", helper);

        // Outer automatic task outer(input logic [7:0] a, output logic [7:0] res) calling helper!
        var outer = db.Create(UhdmObjectType.Task).SetString("VpiName", "outer").SetBool("VpiAutomatic", true);
        outer.Parent = top;
        outer.File = "task_top.sv";
        outer.LineNo = 20;
        var oA = MakeLogicVar(db, outer, "a", 8, file: "task_top.sv", line: 20);
        var oRes = MakeLogicVar(db, outer, "res", 8, file: "task_top.sv", line: 20);
        outer.AddListRef("Io_decls", db.Create(UhdmObjectType.IoDecl).SetString("VpiName", "a").SetInt("VpiDirection", UhdmVpi.VpiInput).SetRef("Expr", oA));
        outer.AddListRef("Io_decls", db.Create(UhdmObjectType.IoDecl).SetString("VpiName", "res").SetInt("VpiDirection", UhdmVpi.VpiOutput).SetRef("Expr", oRes));
        var oBody = db.Create(UhdmObjectType.Begin);
        oBody.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", true)
            .SetRef("Lhs", MakeRef(db, "res", oRes, outer)).SetRef("Rhs", MakeRef(db, "a", oA, outer)));
        var nestedCall = db.Create(UhdmObjectType.TaskCall).SetString("VpiName", "helper").SetRef("Task", helper);
        nestedCall.AddListRef("Tf_call_args", MakeRef(db, "res", oRes, outer));
        nestedCall.AddListRef("Tf_call_args", MakeConst(db, 1, 8, "BIN:00000001"));
        oBody.AddListRef("Stmts", nestedCall);
        oBody.AddListRef("Stmts", db.Create(UhdmObjectType.ReturnStmt));
        outer.SetRef("Stmt", oBody);
        top.AddListRef("Task_funcs", outer);

        // Enum constant
        var enumTs = db.Create(UhdmObjectType.EnumTypespec).SetRef("Base_typespec", MakeLogicTypespec(db, 8, signed: true));
        var enumC = db.Create(UhdmObjectType.EnumConst).SetString("VpiName", "E1").SetInt("VpiSize", 8).SetString("VpiValue", "INT:3");
        enumC.Parent = enumTs;

        // Main process calling outer(x, y), selections, enum_const, $urandom/$random/$urandom_range/$test$plusargs/$fatal/$write, while/do-while
        var procBody = db.Create(UhdmObjectType.Begin);
        var tCall = db.Create(UhdmObjectType.TaskCall).SetString("VpiName", "outer").SetRef("Task", outer);
        tCall.AddListRef("Tf_call_args", MakeRef(db, "x", x, top));
        tCall.AddListRef("Tf_call_args", MakeRef(db, "y", y, top));
        procBody.AddListRef("Stmts", tCall);

        var bs = db.Create(UhdmObjectType.BitSelect).SetRef("VpiIndex", MakeConst(db, 0, 32)).SetRef("Actual_group", x);
        procBody.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", false)
            .SetRef("Lhs", MakeRef(db, "b", b, top)).SetRef("Rhs", bs));

        var ur = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$urandom");
        var rn = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$random");
        var urr = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$urandom_range");
        urr.AddListRef("Tf_call_args", MakeConst(db, 10, 32));
        urr.AddListRef("Tf_call_args", MakeConst(db, 1, 32));
        var plus = db.Create(UhdmObjectType.SysFuncCall).SetString("VpiName", "$test$plusargs");
        plus.AddListRef("Tf_call_args", MakeConst(db, 0, 8, "STRING:FAST", "\"FAST\"", constType: UhdmVpi.VpiStringConst));
        procBody.AddListRef("Stmts", db.Create(UhdmObjectType.Assignment).SetBool("VpiBlocking", false)
            .SetRef("Lhs", MakeRef(db, "y", y, top))
            .SetRef("Rhs", MakeOp(db, UhdmVpi.VpiAddOp, MakeOp(db, UhdmVpi.VpiAddOp, ur, rn), MakeOp(db, UhdmVpi.VpiAddOp, urr, MakeOp(db, UhdmVpi.VpiAddOp, plus, enumC)))));

        var fatalCall = db.Create(UhdmObjectType.SysTaskCall).SetString("VpiName", "$fatal");
        fatalCall.AddListRef("Tf_call_args", MakeConst(db, 2, 32));
        fatalCall.AddListRef("Tf_call_args", MakeConst(db, 0, 8, "STRING:bad=%0d", "\"bad=%0d\"", constType: UhdmVpi.VpiStringConst));
        fatalCall.AddListRef("Tf_call_args", MakeRef(db, "x", x, top));
        procBody.AddListRef("Stmts", db.Create(UhdmObjectType.WhileStmt)
            .SetRef("VpiCondition", MakeConst(db, 0, 1, "BIN:0"))
            .SetRef("VpiStmt", fatalCall));
        procBody.AddListRef("Stmts", db.Create(UhdmObjectType.DoWhile)
            .SetRef("VpiCondition", MakeConst(db, 0, 1, "BIN:0"))
            .SetRef("VpiStmt", db.Create(UhdmObjectType.Begin)));

        var ev = db.Create(UhdmObjectType.EventControl)
            .SetRef("VpiCondition", MakeOp(db, UhdmVpi.VpiPosedgeOp, MakeRef(db, "clk", clk, top)))
            .SetRef("Stmt", procBody);
        top.AddListRef("Process", db.Create(UhdmObjectType.Always).SetInt("VpiAlwaysType", UhdmVpi.VpiAlwaysFF).SetRef("Stmt", ev));

        var restored = UhdmDatabase.Load(db.ToPackedBytes());
        string rtlJson = UhdmRtlLowerer.LowerDatabase(restored, "task_top");
        Assert.That(rtlJson, Does.Contain("\"kind\":\"urandom\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"random\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"urandom_range\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"test_plusargs\",\"prefix\":\"FAST\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"fatal\",\"code\":2"));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"bitselect\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"while\""));
        Assert.That(rtlJson, Does.Contain("\"kind\":\"do\""));
        Assert.That(rtlJson, Does.Contain("00000111")); // captured parameter P = 7
    }

    [Test]
    public void UhdmNormalizer_RejectsUnsupportedConstructs_And_HandlesAlternativeRadices()
    {
        // 1. Extra signal rejected
        var extra = BuildCounterUhdm("c", 8);
        var topMod = extra.Designs[0].Many(UhdmVpi.UhdmtopModules)[0];
        topMod.AddListRef("Variables", MakeLogicVar(extra, topMod, "extra_sig", 4));
        Assert.Throws<InvalidOperationException>(() => UhdmNormalizer.Normalize(extra, "c"));

        // 2. Width > 64 rejected
        var wide = BuildCounterUhdm("c", 65);
        Assert.Throws<InvalidOperationException>(() => UhdmNormalizer.Normalize(wide, "c"));

        // 3. Wrong top name rejected
        Assert.Throws<InvalidOperationException>(() => UhdmNormalizer.Normalize(BuildCounterUhdm("c", 8), "other"));

        // 4. Tagged union and missing union provenance rejected by RtlLowerer
        var db = new UhdmDatabase();
        var lowerer = new UhdmRtlLowerer(db) { UnionTagProvenance = false };
        var u = db.Create(UhdmObjectType.UnionTypespec).SetBool("VpiPacked", true);
        u.AddListRef("Members", db.Create(UhdmObjectType.TypespecMember).SetString("VpiName", "a").SetRef("Typespec", MakeRefTypespec(db, MakeLogicTypespec(db, 4))));
        Assert.Throws<InvalidOperationException>(() => lowerer.AggregateWidth(u, 0));

        lowerer.UnionTagProvenance = true;
        u.SetBool("VpiTagged", true);
        var taggedEx = Assert.Throws<InvalidOperationException>(() => lowerer.AggregateWidth(u, 0));
        Assert.That(taggedEx!.Message, Does.Contain("tagged unions"));

        // 5. JsonEscape handles control characters cleanly
        Assert.That(UhdmRtlLowerer.Q("a\"b\\c\n\r\t\b\f\u0001"), Is.EqualTo("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\""));
    }

    private sealed class FakeSurelogRunner(string sourceUhdm) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string outUhdm = Path.Combine(workingDirectory, "slpp_all", "surelog.uhdm");
            Directory.CreateDirectory(Path.GetDirectoryName(outUhdm)!);
            File.Copy(sourceUhdm, outUhdm, overwrite: true);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
