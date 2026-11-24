using System.Runtime.CompilerServices;
using System.Text;

namespace VeloSim.Uhdm;

/// <summary>
/// Pure .NET 11 representation of any UHDM AST/elaborated node across all 208 concrete UHDM classes.
/// Stores all scalar fields in a single flat unboxed <c>long[]</c> and all reference/string/list fields
/// in a single flat <c>object?[]</c>, with O(1) <c>FrozenDictionary</c> VPI dispatch and direct hot-slot accessors.
/// </summary>
public sealed class UhdmNode
{
    private readonly UhdmClassSpec spec;
    private readonly long[] scalars;
    private readonly object?[] refs;

    internal UhdmNode(UhdmClassSpec spec, ulong uhdmId)
    {
        this.spec = spec;
        UhdmId = uhdmId;
        scalars = spec.TotalScalarSlots > 0 ? new long[spec.TotalScalarSlots] : [];
        refs = spec.TotalRefSlots > 0 ? new object?[spec.TotalRefSlots] : [];
    }

    internal UhdmClassSpec Spec => spec;
    internal long[] Scalars => scalars;
    internal object?[] Refs => refs;

    public UhdmObjectType ObjectType => (UhdmObjectType)spec.UhdmType;
    public int VpiType => spec.VpiType;
    public string ClassName => spec.Name;
    public ulong UhdmId { get; set; }
    public UhdmNode? Parent { get; set; }
    public string File { get; set; } = string.Empty;
    public uint LineNo { get; set; }
    public uint EndLineNo { get; set; }
    public ushort ColumnNo { get; set; }
    public ushort EndColumnNo { get; set; }

    public string Name
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int slot = spec.NameRefSlot;
            return slot >= 0 ? refs[slot] as string ?? string.Empty : string.Empty;
        }
    }

    public string DefName
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int slot = spec.DefNameRefSlot;
            return slot >= 0 ? refs[slot] as string ?? string.Empty : string.Empty;
        }
    }

    public string FullName => GetString(UhdmVpi.VpiFullName);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UhdmNode? One(int vpiRelation)
    {
        if (vpiRelation == UhdmVpi.VpiParent)
            return Parent;
        return spec.FlatVpiRelations.TryGetValue(vpiRelation, out var pair) && pair.SingleRefSlot >= 0
            ? refs[pair.SingleRefSlot] as UhdmNode
            : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IReadOnlyList<UhdmNode> Many(int vpiRelation)
    {
        return spec.FlatVpiRelations.TryGetValue(vpiRelation, out var pair) && pair.VecRefSlot >= 0
            ? refs[pair.VecRefSlot] as List<UhdmNode> ?? (IReadOnlyList<UhdmNode>)[]
            : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt(int vpiProperty)
    {
        switch (vpiProperty)
        {
            case UhdmVpi.VpiType: return VpiType;
            case UhdmVpi.VpiLineNo: return LineNo;
            case UhdmVpi.VpiColumnNo: return ColumnNo;
            case UhdmVpi.VpiEndLineNo: return EndLineNo;
            case UhdmVpi.VpiEndColumnNo: return EndColumnNo;
        }
        return spec.FlatVpiProperties.TryGetValue(vpiProperty, out var b) && b.IsScalar
            ? scalars[b.Slot]
            : 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetBool(int vpiProperty) => GetInt(vpiProperty) != 0;

    public string GetString(int vpiProperty)
    {
        if (vpiProperty == UhdmVpi.VpiFile)
            return File;
        if (vpiProperty == UhdmVpi.VpiName)
            return Name;
        if (vpiProperty == UhdmVpi.VpiDefName)
            return DefName;
        if (vpiProperty == UhdmVpi.VpiFullName)
        {
            int slot = spec.FullNameRefSlot;
            if (slot < 0) return string.Empty;
            string value = refs[slot] as string ?? string.Empty;
            if (value.Length == 0) value = ComputeFullName();
            string localName = Name;
            return value.Length > 0 && value != localName ? value : string.Empty;
        }
        return spec.FlatVpiProperties.TryGetValue(vpiProperty, out var b) && !b.IsScalar
            ? refs[b.Slot] as string ?? string.Empty
            : string.Empty;
    }

    /// <summary>Direct named field read (matches UHDM C++ direct accessors like VpiValue, VpiDelay, VpiTagged, VpiTimeUnit).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetFieldString(string fieldName) =>
        spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && !b.IsScalar
            ? refs[b.Slot] as string ?? string.Empty
            : string.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetFieldInt(string fieldName) =>
        spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && b.IsScalar
            ? scalars[b.Slot]
            : 0L;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetFieldBool(string fieldName) => GetFieldInt(fieldName) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UhdmNode? GetFieldRef(string fieldName) =>
        spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && !b.IsScalar
            ? refs[b.Slot] as UhdmNode
            : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IReadOnlyList<UhdmNode> GetFieldList(string fieldName) =>
        spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && !b.IsScalar
            ? refs[b.Slot] as List<UhdmNode> ?? (IReadOnlyList<UhdmNode>)[]
            : [];

    public UhdmNode SetString(string fieldName, string value)
    {
        if (spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && b.Kind == UhdmFieldKind.Symbol)
        {
            refs[b.Slot] = value;
            return this;
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no string field '{fieldName}'.", nameof(fieldName));
    }

    public UhdmNode SetInt(string fieldName, long value)
    {
        if (spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && b.IsScalar)
        {
            scalars[b.Slot] = b.Kind == UhdmFieldKind.Bool ? (value != 0 ? 1L : 0L) : value;
            return this;
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no integer field '{fieldName}'.", nameof(fieldName));
    }

    public UhdmNode SetBool(string fieldName, bool value) => SetInt(fieldName, value ? 1L : 0L);

    public UhdmNode SetRef(string fieldName, UhdmNode? target)
    {
        if (spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && b.Kind is UhdmFieldKind.ObjRef or UhdmFieldKind.FactoryRef)
        {
            refs[b.Slot] = target;
            return this;
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no reference field '{fieldName}'.", nameof(fieldName));
    }

    public UhdmNode AddListRef(string fieldName, UhdmNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (spec.FlatFieldsByName.TryGetValue(fieldName, out var b) && b.Kind is UhdmFieldKind.ObjList or UhdmFieldKind.FactoryList)
        {
            var list = refs[b.Slot] as List<UhdmNode>;
            if (list is null)
            {
                list = [];
                refs[b.Slot] = list;
            }
            list.Add(target);
            return this;
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no list reference field '{fieldName}'.", nameof(fieldName));
    }

    /// <summary>Enumerates every child node across all reference and list-reference fields in the inheritance chain.</summary>
    internal IEnumerable<(string FieldName, UhdmNode Child)> EnumerateAllRelations()
    {
        var relSlots = spec.AllRelationSlots;
        for (int i = 0; i < relSlots.Length; i++)
        {
            var rel = relSlots[i];
            object? val = refs[rel.RefSlot];
            if (val is null) continue;
            if (!rel.IsList)
            {
                yield return (rel.Name, (UhdmNode)val);
            }
            else
            {
                var children = (List<UhdmNode>)val;
                for (int c = 0; c < children.Count; c++)
                {
                    var child = children[c];
                    if (child is not null)
                        yield return (rel.Name, child);
                }
            }
        }
    }

    internal string ComputeFullName()
    {
        if (ObjectType == UhdmObjectType.ModuleInst && Parent is { ObjectType: UhdmObjectType.ModuleInst })
            return DefName;
        var names = new List<string>();
        UhdmNode? cur = this;
        var visited = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
        bool colon = false;
        while (cur is not null && visited.Add(cur) && visited.Count <= 128)
        {
            if (cur.ObjectType == UhdmObjectType.Design) break;
            if (cur.ObjectType is UhdmObjectType.Package or UhdmObjectType.ClassDefn)
                colon = true;
            string n = cur.Name.Length > 0 ? cur.Name : cur.DefName;
            bool skip = cur.ObjectType is UhdmObjectType.MethodFuncCall or UhdmObjectType.MethodTaskCall
                or UhdmObjectType.FuncCall or UhdmObjectType.TaskCall or UhdmObjectType.SysFuncCall or UhdmObjectType.SysTaskCall
                || cur.Parent?.ObjectType == UhdmObjectType.RefObj;
            if (n.Length > 0 && !skip)
                names.Add(n);
            cur = cur.Parent;
        }
        if (names.Count == 0) return string.Empty;
        names.Reverse();
        var sb = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(colon && i == 1 ? "::" : ".");
            sb.Append(names[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Pure .NET 11 UHDM database container and Cap'n Proto binary serializer/deserializer.
/// Restores real binary .uhdm databases produced by Surelog/UHDM and can also serialize in-memory
/// UHDM graphs back to standard Cap'n Proto packed .uhdm files.
/// </summary>
public sealed class UhdmDatabase
{
    private const string BadSymbol = "@@BAD_SYMBOL@@";
    private readonly Dictionary<uint, List<UhdmNode>> factories = [];
    private ulong nextObjectId;

    public uint Version { get; private set; } = UhdmVpi.FormatVersion;

    public IReadOnlyList<UhdmNode> Designs =>
        factories.TryGetValue((uint)UhdmObjectType.Design, out var list) ? list : [];

    public UhdmNode Create(UhdmObjectType type)
    {
        var spec = UhdmSchema.GetByUhdmType(type);
        var node = new UhdmNode(spec, ++nextObjectId);
        if (!factories.TryGetValue(spec.UhdmType, out var list))
        {
            list = [];
            factories[spec.UhdmType] = list;
        }
        list.Add(node);
        return node;
    }

    public IReadOnlyList<UhdmNode> GetFactory(UhdmObjectType type) =>
        factories.TryGetValue((uint)type, out var list) ? list : [];

    public static UhdmDatabase Load(string path, int maxUnpackedBytes = CapnpPackedStream.DefaultMaxUnpackedBytes,
        long traversalLimitWords = CapnpMessageReader.DefaultTraversalLimitWords, int nestingLimit = CapnpMessageReader.DefaultNestingLimit)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("UHDM file path required.", nameof(path));
        byte[] packed = File.ReadAllBytes(path);
        return Load(packed, maxUnpackedBytes, traversalLimitWords, nestingLimit);
    }

    public static UhdmDatabase Load(ReadOnlySpan<byte> packedBytes, int maxUnpackedBytes = CapnpPackedStream.DefaultMaxUnpackedBytes,
        long traversalLimitWords = CapnpMessageReader.DefaultTraversalLimitWords, int nestingLimit = CapnpMessageReader.DefaultNestingLimit)
    {
        var reader = CapnpMessageReader.FromPacked(packedBytes, maxUnpackedBytes, traversalLimitWords, nestingLimit);
        var root = reader.GetRootStruct();
        if (root.IsNull)
            throw new InvalidDataException("UHDM Cap'n Proto root struct is null.");

        uint version = root.ReadUInt32(0);
        if (version != UhdmVpi.FormatVersion)
            throw new InvalidDataException($"Unsupported UHDM binary version {version} (expected {UhdmVpi.FormatVersion}).");

        var db = new UhdmDatabase
        {
            Version = version,
            nextObjectId = root.ReadUInt32(4),
        };

        var symbolList = root.ReadList(UhdmSchema.RootSymbolsPointerSlot);
        var symbols = new string[symbolList.Count];
        for (int i = 0; i < symbolList.Count; i++)
            symbols[i] = symbolList.ReadText(i);

        // Pass 1: Pre-allocate all concrete factory objects so forward/cyclic references resolve cleanly.
        var factoryLists = new List<(uint UhdmType, CapnpList List, List<UhdmNode> Nodes)>();
        var concrete = UhdmSchema.ConcreteClasses;
        for (int s = 0; s < concrete.Count; s++)
        {
            var spec = concrete[s];
            if (spec.RootFactoryPointerSlot < 0) continue;
            var capList = root.ReadList(spec.RootFactoryPointerSlot);
            if (capList.Count == 0) continue;
            var nodes = new List<UhdmNode>(capList.Count);
            for (int i = 0; i < capList.Count; i++)
                nodes.Add(new UhdmNode(spec, 0));
            db.factories[spec.UhdmType] = nodes;
            factoryLists.Add((spec.UhdmType, capList, nodes));
        }

        // Pass 2: Populate every object's flat unboxed scalar/ref arrays and Any metadata.
        for (int f = 0; f < factoryLists.Count; f++)
        {
            var (_, capList, nodes) = factoryLists[f];
            for (int i = 0; i < capList.Count; i++)
                PopulateChain(db, symbols, nodes[i], capList.ReadStruct(i));
        }

        return db;
    }

    private static void PopulateChain(UhdmDatabase db, string[] symbols, UhdmNode node, CapnpStruct topStruct)
    {
        var cur = topStruct;
        var rootSpec = node.Spec;
        var chain = rootSpec.Chain;
        var scalarBases = rootSpec.LevelScalarBase;
        var refBases = rootSpec.LevelRefBase;
        var scalars = node.Scalars;
        var refs = node.Refs;

        for (int level = 0; level < chain.Length; level++)
        {
            var fields = chain[level].Fields;
            int sBase = scalarBases[level];
            int rBase = refBases[level];

            for (int f = 0; f < fields.Length; f++)
            {
                var field = fields[f];
                switch (field.Kind)
                {
                    case UhdmFieldKind.Symbol:
                        refs[rBase + field.LocalSlot] = ResolveSymbol(symbols, cur.ReadUInt64(field.Offset));
                        break;
                    case UhdmFieldKind.Bool:
                        scalars[sBase + field.LocalSlot] = cur.ReadBool(field.Offset) ? 1L : 0L;
                        break;
                    case UhdmFieldKind.Int64:
                        scalars[sBase + field.LocalSlot] = cur.ReadInt64(field.Offset);
                        break;
                    case UhdmFieldKind.UInt64:
                        scalars[sBase + field.LocalSlot] = unchecked((long)cur.ReadUInt64(field.Offset));
                        break;
                    case UhdmFieldKind.UInt32:
                        scalars[sBase + field.LocalSlot] = cur.ReadUInt32(field.Offset);
                        break;
                    case UhdmFieldKind.UInt16:
                        scalars[sBase + field.LocalSlot] = cur.ReadUInt16(field.Offset);
                        break;
                    case UhdmFieldKind.ObjRef:
                        refs[rBase + field.LocalSlot] = ResolveObjIndex(db, cur.ReadStruct(field.Offset));
                        break;
                    case UhdmFieldKind.FactoryRef:
                        refs[rBase + field.LocalSlot] = ResolveFactoryIndex(db, field.TargetUhdmType, cur.ReadUInt64(field.Offset));
                        break;
                    case UhdmFieldKind.ObjList:
                    {
                        var list = cur.ReadList(field.Offset);
                        if (list.Count > 0)
                        {
                            var result = new List<UhdmNode>(list.Count);
                            for (int k = 0; k < list.Count; k++)
                            {
                                var target = ResolveObjIndex(db, list.ReadStruct(k));
                                if (target is not null) result.Add(target);
                            }
                            refs[rBase + field.LocalSlot] = result;
                        }
                        break;
                    }
                    case UhdmFieldKind.FactoryList:
                    {
                        var list = cur.ReadList(field.Offset);
                        if (list.Count > 0)
                        {
                            var result = new List<UhdmNode>(list.Count);
                            for (int k = 0; k < list.Count; k++)
                            {
                                var target = ResolveFactoryIndex(db, field.TargetUhdmType, list.ReadUInt64(k));
                                if (target is not null) result.Add(target);
                            }
                            refs[rBase + field.LocalSlot] = result;
                        }
                        break;
                    }
                }
            }
            cur = cur.ReadStruct(0);
        }

        if (!cur.IsNull)
        {
            node.UhdmId = cur.ReadUInt64(UhdmSchema.AnyUhdmIdByteOffset);
            node.Parent = ResolveObjIndex(db, cur.ReadStruct(UhdmSchema.AnyVpiParentPtrSlot));
            node.File = ResolveSymbol(symbols, cur.ReadUInt64(UhdmSchema.AnyVpiFileByteOffset));
            node.LineNo = cur.ReadUInt32(UhdmSchema.AnyVpiLineNoByteOffset);
            node.EndLineNo = cur.ReadUInt32(UhdmSchema.AnyVpiEndLineNoByteOffset);
            node.ColumnNo = cur.ReadUInt16(UhdmSchema.AnyVpiColumnNoByteOffset);
            node.EndColumnNo = cur.ReadUInt16(UhdmSchema.AnyVpiEndColumnNoByteOffset);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ResolveSymbol(string[] symbols, ulong rawId)
    {
        if (rawId == 0 || rawId >= (ulong)symbols.Length) return string.Empty;
        string s = symbols[(int)rawId];
        return ReferenceEquals(s, BadSymbol) || s == BadSymbol ? string.Empty : s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UhdmNode? ResolveObjIndex(UhdmDatabase db, CapnpStruct objIndex)
    {
        if (objIndex.IsNull) return null;
        ulong oneBased = objIndex.ReadUInt64(UhdmSchema.ObjIndexByteOffset);
        if (oneBased == 0) return null;
        uint type = objIndex.ReadUInt32(UhdmSchema.ObjTypeByteOffset);
        return ResolveFactoryIndex(db, type, oneBased);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UhdmNode? ResolveFactoryIndex(UhdmDatabase db, uint uhdmType, ulong oneBased)
    {
        if (oneBased == 0) return null;
        if (!db.factories.TryGetValue(uhdmType, out var list))
            return null;
        ulong zeroBased = oneBased - 1;
        if (zeroBased >= (ulong)list.Count)
            throw new InvalidDataException($"UHDM reference index {oneBased} out of bounds for factory type {uhdmType} (count {list.Count}).");
        return list[(int)zeroBased];
    }

    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("UHDM output path required.", nameof(path));
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ToPackedBytes());
    }

    public byte[] ToPackedBytes()
    {
        var symbolMap = new Dictionary<string, ulong>(StringComparer.Ordinal) { [BadSymbol] = 0 };
        var symbolList = new List<string> { BadSymbol };
        ulong Intern(string? value)
        {
            if (string.IsNullOrEmpty(value) || value == BadSymbol) return 0UL;
            if (symbolMap.TryGetValue(value, out ulong id)) return id;
            id = (ulong)symbolList.Count;
            symbolList.Add(value);
            symbolMap[value] = id;
            return id;
        }

        var nodeIndex = new Dictionary<UhdmNode, ulong>(ReferenceEqualityComparer.Instance);
        foreach (var (_, list) in factories)
        {
            for (int i = 0; i < list.Count; i++)
                nodeIndex[list[i]] = (ulong)(i + 1);
        }

        var writer = new CapnpMessageWriter();
        int rootStart = writer.AllocateStruct(0, UhdmSchema.RootDataWords, UhdmSchema.RootPointerCount);
        writer.SetUInt32(rootStart, 0, Version);
        writer.SetUInt32(rootStart, 4, (uint)nextObjectId);

        foreach (var spec in UhdmSchema.ConcreteClasses)
        {
            if (spec.RootFactoryPointerSlot < 0) continue;
            if (!factories.TryGetValue(spec.UhdmType, out var list) || list.Count == 0) continue;

            int listPtrWord = rootStart + UhdmSchema.RootDataWords + spec.RootFactoryPointerSlot;
            int firstElemWord = writer.AllocateCompositeStructList(listPtrWord, list.Count, spec.DataWords, spec.PointerCount);
            int stride = spec.DataWords + spec.PointerCount;
            for (int i = 0; i < list.Count; i++)
                WriteNodeChain(writer, list[i], firstElemWord + i * stride, Intern, nodeIndex);
        }

        int symPtrWord = rootStart + UhdmSchema.RootDataWords + UhdmSchema.RootSymbolsPointerSlot;
        int symListStart = writer.AllocatePointerList(symPtrWord, symbolList.Count);
        for (int i = 0; i < symbolList.Count; i++)
            writer.WriteText(symListStart + i, symbolList[i]);

        return writer.ToPackedBytes();
    }

    private static void WriteNodeChain(CapnpMessageWriter writer, UhdmNode node, int structStartWord,
        Func<string?, ulong> intern, Dictionary<UhdmNode, ulong> nodeIndex)
    {
        int curStart = structStartWord;
        var rootSpec = node.Spec;
        var chain = rootSpec.Chain;
        var scalarBases = rootSpec.LevelScalarBase;
        var refBases = rootSpec.LevelRefBase;
        var scalars = node.Scalars;
        var refs = node.Refs;

        for (int level = 0; level < chain.Length; level++)
        {
            var spec = chain[level];
            var fields = spec.Fields;
            int sBase = scalarBases[level];
            int rBase = refBases[level];
            int ptrBase = curStart + spec.DataWords;

            for (int f = 0; f < fields.Length; f++)
            {
                var field = fields[f];
                if (field.IsScalar)
                {
                    long sv = scalars[sBase + field.LocalSlot];
                    if (sv == 0) continue;
                    switch (field.Kind)
                    {
                        case UhdmFieldKind.Bool: writer.SetBool(curStart, field.Offset, true); break;
                        case UhdmFieldKind.Int64: writer.SetInt64(curStart, field.Offset, sv); break;
                        case UhdmFieldKind.UInt64: writer.SetUInt64(curStart, field.Offset, unchecked((ulong)sv)); break;
                        case UhdmFieldKind.UInt32: writer.SetUInt32(curStart, field.Offset, unchecked((uint)sv)); break;
                        case UhdmFieldKind.UInt16: writer.SetUInt16(curStart, field.Offset, unchecked((ushort)sv)); break;
                    }
                    continue;
                }

                object? val = refs[rBase + field.LocalSlot];
                if (val is null) continue;

                switch (field.Kind)
                {
                    case UhdmFieldKind.Symbol:
                        writer.SetUInt64(curStart, field.Offset, intern((string)val));
                        break;
                    case UhdmFieldKind.ObjRef:
                    {
                        var target = (UhdmNode)val;
                        if (nodeIndex.TryGetValue(target, out ulong idx))
                        {
                            int objIdxWord = writer.AllocateStruct(ptrBase + field.Offset, UhdmSchema.ObjIndexDataWords, UhdmSchema.ObjIndexPointerCount);
                            writer.SetUInt64(objIdxWord, UhdmSchema.ObjIndexByteOffset, idx);
                            writer.SetUInt32(objIdxWord, UhdmSchema.ObjTypeByteOffset, target.Spec.UhdmType);
                        }
                        break;
                    }
                    case UhdmFieldKind.FactoryRef:
                    {
                        var target = (UhdmNode)val;
                        if (nodeIndex.TryGetValue(target, out ulong idx))
                            writer.SetUInt64(curStart, field.Offset, idx);
                        break;
                    }
                    case UhdmFieldKind.ObjList:
                    {
                        var items = (List<UhdmNode>)val;
                        if (items.Count > 0)
                        {
                            int elemStart = writer.AllocateCompositeStructList(ptrBase + field.Offset, items.Count, UhdmSchema.ObjIndexDataWords, UhdmSchema.ObjIndexPointerCount);
                            for (int k = 0; k < items.Count; k++)
                            {
                                var target = items[k];
                                if (nodeIndex.TryGetValue(target, out ulong idx))
                                {
                                    int w = elemStart + k * UhdmSchema.ObjIndexDataWords;
                                    writer.SetUInt64(w, UhdmSchema.ObjIndexByteOffset, idx);
                                    writer.SetUInt32(w, UhdmSchema.ObjTypeByteOffset, target.Spec.UhdmType);
                                }
                            }
                        }
                        break;
                    }
                    case UhdmFieldKind.FactoryList:
                    {
                        var items = (List<UhdmNode>)val;
                        if (items.Count > 0)
                        {
                            var raw = new ulong[items.Count];
                            for (int k = 0; k < items.Count; k++)
                                raw[k] = nodeIndex.TryGetValue(items[k], out ulong idx) ? idx : 0UL;
                            writer.AllocateUInt64List(ptrBase + field.Offset, raw);
                        }
                        break;
                    }
                }
            }

            if (level + 1 < chain.Length)
            {
                var nextSpec = chain[level + 1];
                curStart = writer.AllocateStruct(ptrBase, nextSpec.DataWords, nextSpec.PointerCount);
            }
            else
            {
                int anyStart = writer.AllocateStruct(ptrBase, UhdmSchema.AnyDataWords, UhdmSchema.AnyPointerCount);
                writer.SetUInt64(anyStart, UhdmSchema.AnyUhdmIdByteOffset, node.UhdmId);
                writer.SetUInt64(anyStart, UhdmSchema.AnyVpiFileByteOffset, intern(node.File));
                writer.SetUInt32(anyStart, UhdmSchema.AnyVpiLineNoByteOffset, node.LineNo);
                writer.SetUInt32(anyStart, UhdmSchema.AnyVpiEndLineNoByteOffset, node.EndLineNo);
                writer.SetUInt16(anyStart, UhdmSchema.AnyVpiColumnNoByteOffset, node.ColumnNo);
                writer.SetUInt16(anyStart, UhdmSchema.AnyVpiEndColumnNoByteOffset, node.EndColumnNo);
                if (node.Parent is not null && nodeIndex.TryGetValue(node.Parent, out ulong parentIdx))
                {
                    int parentObjWord = writer.AllocateStruct(anyStart + UhdmSchema.AnyDataWords + UhdmSchema.AnyVpiParentPtrSlot,
                        UhdmSchema.ObjIndexDataWords, UhdmSchema.ObjIndexPointerCount);
                    writer.SetUInt64(parentObjWord, UhdmSchema.ObjIndexByteOffset, parentIdx);
                    writer.SetUInt32(parentObjWord, UhdmSchema.ObjTypeByteOffset, node.Parent.Spec.UhdmType);
                }
            }
        }
    }
}
