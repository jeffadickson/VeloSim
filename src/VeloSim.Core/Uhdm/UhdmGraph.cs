using System.Text;

namespace VeloSim.Uhdm;

/// <summary>
/// Pure .NET 11 representation of any UHDM AST/elaborated node across all 208 concrete UHDM classes.
/// Provides both VPI-compatible traversal (<see cref="One"/>, <see cref="Many"/>, <see cref="GetInt"/>,
/// <see cref="GetString"/>) and direct named member access matching libuhdm's C++ API.
/// </summary>
public sealed class UhdmNode
{
    private readonly UhdmClassSpec spec;
    private readonly UhdmClassSpec[] chain; // [0] = concrete class, [^1] = highest base below Any
    private readonly object?[][] levelValues;

    internal UhdmNode(UhdmClassSpec spec, ulong uhdmId)
    {
        this.spec = spec;
        UhdmId = uhdmId;

        var list = new List<UhdmClassSpec>(6);
        for (var cur = spec; cur is not null; cur = cur.BaseClass)
            list.Add(cur);
        chain = list.ToArray();
        levelValues = new object?[chain.Length][];
        for (int i = 0; i < chain.Length; i++)
            levelValues[i] = new object?[chain[i].Fields.Length];
    }

    internal UhdmClassSpec Spec => spec;
    internal UhdmClassSpec[] Chain => chain;
    internal object?[][] LevelValues => levelValues;

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

    public string Name => GetString(UhdmVpi.VpiName);
    public string DefName => GetString(UhdmVpi.VpiDefName);
    public string FullName => GetString(UhdmVpi.VpiFullName);

    public UhdmNode? One(int vpiRelation)
    {
        if (vpiRelation == UhdmVpi.VpiParent)
            return Parent;
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].VpiRelationFieldIndex.TryGetValue(vpiRelation, out var pair) && pair.SingleFieldIndex >= 0)
                return levelValues[i][pair.SingleFieldIndex] as UhdmNode;
        }
        return null;
    }

    public IReadOnlyList<UhdmNode> Many(int vpiRelation)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].VpiRelationFieldIndex.TryGetValue(vpiRelation, out var pair) && pair.VectorFieldIndex >= 0)
                return FilterNonNull(levelValues[i][pair.VectorFieldIndex]);
        }
        return [];
    }

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
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].VpiPropertyFieldIndex.TryGetValue(vpiProperty, out int fieldIdx))
            {
                return levelValues[i][fieldIdx] switch
                {
                    bool b => b ? 1L : 0L,
                    long l => l,
                    ulong u => unchecked((long)u),
                    uint u32 => u32,
                    ushort u16 => u16,
                    _ => 0L,
                };
            }
        }
        return 0L;
    }

    public bool GetBool(int vpiProperty) => GetInt(vpiProperty) != 0;

    public string GetString(int vpiProperty)
    {
        if (vpiProperty == UhdmVpi.VpiFile)
            return File;
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].VpiPropertyFieldIndex.TryGetValue(vpiProperty, out int fieldIdx))
            {
                string value = levelValues[i][fieldIdx] as string ?? string.Empty;
                if (vpiProperty == UhdmVpi.VpiFullName)
                {
                    if (value.Length == 0)
                        value = ComputeFullName();
                    string localName = GetString(UhdmVpi.VpiName);
                    return value.Length > 0 && value != localName ? value : string.Empty;
                }
                return value;
            }
        }
        return string.Empty;
    }

    /// <summary>Direct named field read (matches UHDM C++ direct accessors like VpiValue, VpiDelay, VpiTagged, VpiTimeUnit).</summary>
    public string GetFieldString(string fieldName)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
                return levelValues[i][idx] as string ?? string.Empty;
        }
        return string.Empty;
    }

    public long GetFieldInt(string fieldName)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
            {
                return levelValues[i][idx] switch
                {
                    bool b => b ? 1L : 0L,
                    long l => l,
                    ulong u => unchecked((long)u),
                    uint u32 => u32,
                    ushort u16 => u16,
                    _ => 0L,
                };
            }
        }
        return 0L;
    }

    public bool GetFieldBool(string fieldName) => GetFieldInt(fieldName) != 0;

    public UhdmNode? GetFieldRef(string fieldName)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
                return levelValues[i][idx] as UhdmNode;
        }
        return null;
    }

    public IReadOnlyList<UhdmNode> GetFieldList(string fieldName)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
                return FilterNonNull(levelValues[i][idx]);
        }
        return [];
    }

    internal IReadOnlyList<UhdmNode?> GetFieldListWithNulls(string fieldName)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
                return levelValues[i][idx] as List<UhdmNode?> ?? (IReadOnlyList<UhdmNode?>)[];
        }
        return [];
    }

    private static IReadOnlyList<UhdmNode> FilterNonNull(object? raw)
    {
        if (raw is not List<UhdmNode?> list || list.Count == 0)
            return [];
        bool hasNull = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
            {
                hasNull = true;
                break;
            }
        }
        if (!hasNull)
            return (IReadOnlyList<UhdmNode>)list!;
        var filtered = new List<UhdmNode>(list.Count);
        foreach (var item in list)
        {
            if (item is not null)
                filtered.Add(item);
        }
        return filtered;
    }

    public UhdmNode SetString(string fieldName, string value)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
            {
                levelValues[i][idx] = value;
                return this;
            }
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no string field '{fieldName}'.", nameof(fieldName));
    }

    public UhdmNode SetInt(string fieldName, long value)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
            {
                levelValues[i][idx] = chain[i].Fields[idx].Kind switch
                {
                    UhdmFieldKind.Bool => value != 0,
                    UhdmFieldKind.UInt64 => unchecked((ulong)value),
                    UhdmFieldKind.UInt32 => unchecked((uint)value),
                    UhdmFieldKind.UInt16 => unchecked((ushort)value),
                    _ => value,
                };
                return this;
            }
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no integer field '{fieldName}'.", nameof(fieldName));
    }

    public UhdmNode SetBool(string fieldName, bool value) => SetInt(fieldName, value ? 1L : 0L);

    public UhdmNode SetRef(string fieldName, UhdmNode? target)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
            {
                levelValues[i][idx] = target;
                return this;
            }
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no reference field '{fieldName}'.", nameof(fieldName));
    }

    public UhdmNode AddListRef(string fieldName, UhdmNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return AddListRefOrNull(fieldName, target);
    }

    internal UhdmNode AddListRefOrNull(string fieldName, UhdmNode? target)
    {
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i].FieldIndexByName.TryGetValue(fieldName, out int idx))
            {
                var list = levelValues[i][idx] as List<UhdmNode?>;
                if (list is null)
                {
                    list = [];
                    levelValues[i][idx] = list;
                }
                list.Add(target);
                return this;
            }
        }
        throw new ArgumentException($"UHDM class '{ClassName}' has no list reference field '{fieldName}'.", nameof(fieldName));
    }

    /// <summary>Enumerates every child node across all reference and list-reference fields in the inheritance chain.</summary>
    internal IEnumerable<(string FieldName, UhdmNode Child)> EnumerateAllRelations()
    {
        for (int i = chain.Length - 1; i >= 0; i--)
        {
            var fields = chain[i].Fields;
            var values = levelValues[i];
            for (int f = 0; f < fields.Length; f++)
            {
                var kind = fields[f].Kind;
                if (kind is UhdmFieldKind.ObjRef or UhdmFieldKind.FactoryRef)
                {
                    if (values[f] is UhdmNode child)
                        yield return (fields[f].Name, child);
                }
                else if (kind is UhdmFieldKind.ObjList or UhdmFieldKind.FactoryList)
                {
                    if (values[f] is List<UhdmNode?> children)
                    {
                        foreach (var child in children)
                        {
                            if (child is not null)
                                yield return (fields[f].Name, child);
                        }
                    }
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

        // Load symbol table (index 0 is @@BAD_SYMBOL@@).
        var symbolList = root.ReadList(UhdmSchema.RootSymbolsPointerSlot);
        var symbols = new string[symbolList.Count];
        for (int i = 0; i < symbolList.Count; i++)
            symbols[i] = symbolList.ReadText(i);

        // Pass 1: Pre-allocate all concrete factory objects so forward/cyclic references resolve cleanly.
        var factoryLists = new Dictionary<uint, CapnpList>();
        foreach (var spec in UhdmSchema.ConcreteClasses)
        {
            if (spec.RootFactoryPointerSlot < 0) continue;
            var capList = root.ReadList(spec.RootFactoryPointerSlot);
            if (capList.Count == 0) continue;
            factoryLists[spec.UhdmType] = capList;
            var nodes = new List<UhdmNode>(capList.Count);
            for (int i = 0; i < capList.Count; i++)
                nodes.Add(new UhdmNode(spec, 0));
            db.factories[spec.UhdmType] = nodes;
        }

        // Pass 2: Populate every object's Any base and inheritance chain fields.
        foreach (var (uhdmType, capList) in factoryLists)
        {
            var nodes = db.factories[uhdmType];
            for (int i = 0; i < capList.Count; i++)
            {
                var node = nodes[i];
                var curStruct = capList.ReadStruct(i);
                PopulateChain(db, symbols, node, curStruct);
            }
        }

        return db;
    }

    private static void PopulateChain(UhdmDatabase db, string[] symbols, UhdmNode node, CapnpStruct topStruct)
    {
        var cur = topStruct;
        for (int level = 0; level < node.Chain.Length; level++)
        {
            var spec = node.Chain[level];
            var values = node.LevelValues[level];
            for (int f = 0; f < spec.Fields.Length; f++)
            {
                var field = spec.Fields[f];
                switch (field.Kind)
                {
                    case UhdmFieldKind.Symbol:
                        values[f] = ResolveSymbol(symbols, cur.ReadUInt64(field.Offset));
                        break;
                    case UhdmFieldKind.Bool:
                        values[f] = cur.ReadBool(field.Offset);
                        break;
                    case UhdmFieldKind.Int64:
                        values[f] = cur.ReadInt64(field.Offset);
                        break;
                    case UhdmFieldKind.UInt64:
                        values[f] = cur.ReadUInt64(field.Offset);
                        break;
                    case UhdmFieldKind.UInt32:
                        values[f] = cur.ReadUInt32(field.Offset);
                        break;
                    case UhdmFieldKind.UInt16:
                        values[f] = cur.ReadUInt16(field.Offset);
                        break;
                    case UhdmFieldKind.ObjRef:
                    {
                        var idxStruct = cur.ReadStruct(field.Offset);
                        values[f] = ResolveObjIndex(db, idxStruct);
                        break;
                    }
                    case UhdmFieldKind.FactoryRef:
                    {
                        ulong oneBased = cur.ReadUInt64(field.Offset);
                        values[f] = ResolveFactoryIndex(db, field.TargetUhdmType, oneBased);
                        break;
                    }
                    case UhdmFieldKind.ObjList:
                    {
                        var list = cur.ReadList(field.Offset);
                        if (list.Count > 0)
                        {
                            var result = new List<UhdmNode?>(list.Count);
                            for (int k = 0; k < list.Count; k++)
                                result.Add(ResolveObjIndex(db, list.ReadStruct(k)));
                            values[f] = result;
                        }
                        break;
                    }
                    case UhdmFieldKind.FactoryList:
                    {
                        var list = cur.ReadList(field.Offset);
                        if (list.Count > 0)
                        {
                            var result = new List<UhdmNode?>(list.Count);
                            for (int k = 0; k < list.Count; k++)
                            {
                                var target = ResolveFactoryIndex(db, field.TargetUhdmType, list.ReadUInt64(k));
                                if (target is not null) result.Add(target);
                            }
                            values[f] = result;
                        }
                        break;
                    }
                }
            }
            // Pointer slot 0 in every non-Any UHDM struct is always `base @0 : Basename;`.
            cur = cur.ReadStruct(0);
        }

        // Now `cur` is the `Any` struct at the bottom of the inheritance chain.
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

    private static string ResolveSymbol(string[] symbols, ulong rawId)
    {
        if (rawId == 0 || rawId >= (ulong)symbols.Length) return string.Empty;
        string s = symbols[(int)rawId];
        return s == BadSymbol ? string.Empty : s;
    }

    private static UhdmNode? ResolveObjIndex(UhdmDatabase db, CapnpStruct objIndex)
    {
        if (objIndex.IsNull) return null;
        ulong oneBased = objIndex.ReadUInt64(UhdmSchema.ObjIndexByteOffset);
        if (oneBased == 0) return null;
        uint type = objIndex.ReadUInt32(UhdmSchema.ObjTypeByteOffset);
        return ResolveFactoryIndex(db, type, oneBased);
    }

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

        // Map every node to its 1-based factory index.
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

        // Write root `designs @2 : List(Design)` (slot 0) matching UHDM::Serializer::Save.
        var designs = Designs;
        if (designs.Count > 0)
        {
            var designSpec = UhdmSchema.GetByUhdmType(UhdmObjectType.Design);
            int designNameOffset = designSpec.Fields[designSpec.FieldIndexByName["VpiName"]].Offset;
            int designsPtrWord = rootStart + UhdmSchema.RootDataWords + UhdmSchema.RootDesignsPointerSlot;
            int firstDesignWord = writer.AllocateCompositeStructList(designsPtrWord, designs.Count, designSpec.DataWords, designSpec.PointerCount);
            int designStride = designSpec.DataWords + designSpec.PointerCount;
            for (int i = 0; i < designs.Count; i++)
                writer.SetUInt64(firstDesignWord + i * designStride, designNameOffset, Intern(designs[i].Name));
        }

        // Write every non-empty factory list.
        foreach (var spec in UhdmSchema.ConcreteClasses)
        {
            if (spec.RootFactoryPointerSlot < 0) continue;
            if (!factories.TryGetValue(spec.UhdmType, out var list) || list.Count == 0) continue;

            int listPtrWord = rootStart + UhdmSchema.RootDataWords + spec.RootFactoryPointerSlot;
            int firstElemWord = writer.AllocateCompositeStructList(listPtrWord, list.Count, spec.DataWords, spec.PointerCount);
            int stride = spec.DataWords + spec.PointerCount;
            for (int i = 0; i < list.Count; i++)
            {
                WriteNodeChain(writer, list[i], firstElemWord + i * stride, Intern, nodeIndex);
            }
        }

        // Write symbol table last (after all strings have been interned).
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
        for (int level = 0; level < node.Chain.Length; level++)
        {
            var spec = node.Chain[level];
            var values = node.LevelValues[level];
            int ptrBase = curStart + spec.DataWords;

            for (int f = 0; f < spec.Fields.Length; f++)
            {
                var field = spec.Fields[f];
                object? val = values[f];
                if (val is null) continue;

                switch (field.Kind)
                {
                    case UhdmFieldKind.Symbol:
                        writer.SetUInt64(curStart, field.Offset, intern((string)val));
                        break;
                    case UhdmFieldKind.Bool:
                        writer.SetBool(curStart, field.Offset, (bool)val);
                        break;
                    case UhdmFieldKind.Int64:
                        writer.SetInt64(curStart, field.Offset, (long)val);
                        break;
                    case UhdmFieldKind.UInt64:
                        writer.SetUInt64(curStart, field.Offset, (ulong)val);
                        break;
                    case UhdmFieldKind.UInt32:
                        writer.SetUInt32(curStart, field.Offset, (uint)val);
                        break;
                    case UhdmFieldKind.UInt16:
                        writer.SetUInt16(curStart, field.Offset, (ushort)val);
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
                        var items = (List<UhdmNode?>)val;
                        if (items.Count > 0)
                        {
                            int elemStart = writer.AllocateCompositeStructList(ptrBase + field.Offset, items.Count, UhdmSchema.ObjIndexDataWords, UhdmSchema.ObjIndexPointerCount);
                            for (int k = 0; k < items.Count; k++)
                            {
                                var target = items[k];
                                if (target is not null && nodeIndex.TryGetValue(target, out ulong idx))
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
                        var items = (List<UhdmNode?>)val;
                        if (items.Count > 0)
                        {
                            var raw = new ulong[items.Count];
                            for (int k = 0; k < items.Count; k++)
                                raw[k] = items[k] is { } item && nodeIndex.TryGetValue(item, out ulong idx) ? idx : 0UL;
                            writer.AllocateUInt64List(ptrBase + field.Offset, raw);
                        }
                        break;
                    }
                }
            }

            // Allocate next level (`base @0`) at pointer slot 0.
            if (level + 1 < node.Chain.Length)
            {
                var nextSpec = node.Chain[level + 1];
                curStart = writer.AllocateStruct(ptrBase, nextSpec.DataWords, nextSpec.PointerCount);
            }
            else
            {
                // Allocate `Any` struct at pointer slot 0.
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
