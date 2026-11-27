using System.Globalization;
using System.Text;

namespace VeloSim.Uhdm;

/// <summary>Resource budgets matching <c>native/uhdm/rtl.cpp</c> bounded and <c>VELOSIM_LARGE_DESIGN=1</c> profiles.</summary>
public sealed record UhdmRtlLimits(
    int MaxWordBits = 256,
    int MaxSignals = 256,
    int MaxMemories = 16,
    ulong MaxMemDepth = 256,
    int MaxScopes = 256,
    int MaxProcesses = 256,
    int MaxNodes = 20_000,
    int MaxJsonBytes = 4 * 1024 * 1024,
    int MaxDepth = 64,
    int MaxAuditNodes = 100_000)
{
    public static UhdmRtlLimits Bounded { get; } = new();
    public static UhdmRtlLimits Large { get; } = new(
        MaxWordBits: 512,
        MaxSignals: 65_536,
        MaxMemories: 1_024,
        MaxMemDepth: 1_048_576,
        MaxScopes: 4_096,
        MaxProcesses: 65_536,
        MaxNodes: 1_000_000,
        MaxJsonBytes: 64 * 1024 * 1024,
        MaxDepth: 128,
        MaxAuditNodes: 2_000_000);

    public static UhdmRtlLimits FromEnvironment() =>
        Environment.GetEnvironmentVariable("VELOSIM_LARGE_DESIGN") == "1" ? Large : Bounded;
}

/// <summary>
/// Pure .NET 11 / C# 15 port of <c>native/uhdm/rtl.cpp</c> and its supporting headers
/// (<c>rtl_constants.h</c>, <c>rtl_original_expr.h</c>, <c>rtl_types.h</c>, <c>rtl_audit.h</c>,
/// <c>rtl_subroutines.h</c>, <c>rtl_flatten.h</c>).
/// Lowers a binary UHDM database into canonical <c>velosim.rtl.v1</c> structural RTL IR.
/// Thread-safe and re-entrant (all traversal state is per-instance).
/// </summary>
public sealed partial class UhdmRtlLowerer
{
    private readonly UhdmDatabase db;
    private readonly UhdmRtlLimits limits;
    private UhdmNode? mod;
    private int nodes;
    private readonly Dictionary<UhdmNode, string> flatSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly SortedDictionary<string, UhdmNode> flatDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<UhdmNode>> flatDeclsByCanonicalKey = new(StringComparer.Ordinal);
    private int declarationAliasChecks;
    private Dictionary<string, string> activeSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> clockAliases = new(StringComparer.Ordinal);
    private bool timedLowering;
    private int timedPrecision;
    private readonly SortedSet<string> flatNamedEvents = new(StringComparer.Ordinal);

    internal UhdmRtlLowerer(UhdmDatabase db, UhdmRtlLimits? limits = null)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.limits = limits ?? UhdmRtlLimits.FromEnvironment();
    }

    public static string LowerFile(string uhdmPath, string top, UhdmRtlLimits? limits = null)
    {
        var database = UhdmDatabase.Load(uhdmPath);
        return LowerDatabase(database, top, limits);
    }

    public static void LowerFileTo(string uhdmPath, string outputPath, string top, UhdmRtlLimits? limits = null)
    {
        string json = LowerFile(uhdmPath, top, limits);
        if (Path.GetDirectoryName(Path.GetFullPath(outputPath)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
    }

    public static string LowerDatabase(UhdmDatabase database, string top, UhdmRtlLimits? limits = null)
    {
        var lowerer = new UhdmRtlLowerer(database, limits);
        return lowerer.Run(top);
    }

    private string Run(string top)
    {
        var designs = db.Designs;
        Need(designs.Count == 1, "one design required");
        var design = designs[0];
        var modules = design.Many(UhdmVpi.UhdmtopModules);
        Need(modules.Count == 1, "one top required");
        mod = modules[0];
        Need(mod.Name == top || mod.Name == "work@" + top, "top mismatch");

        foreach (var spec in design.Many(UhdmVpi.VpiTypedef))
            if (spec.VpiType == UhdmVpi.VpiVoidTypespec && spec.Name == "velosim frontend capability: union tags v1")
                UnionTagProvenance = true;

        foreach (var original in design.Many(UhdmVpi.UhdmallModules))
        {
            CollectOriginalExpressions(original);
            CollectParameterSyntax(original);
            CollectPortInitializers(original);
        }

        var audit = new UhdmRtlAudit(limits.MaxAuditNodes, limits.MaxDepth);
        audit.Audit(mod);
        timedLowering = audit.Counts.ContainsKey(UhdmVpi.VpiDelayControl)
            || audit.Counts.ContainsKey(UhdmVpi.VpiWait)
            || audit.Counts.ContainsKey(UhdmVpi.VpiFork)
            || audit.Counts.ContainsKey(UhdmVpi.VpiWaitFork)
            || audit.Counts.ContainsKey(UhdmVpi.VpiDisableFork)
            || audit.Counts.ContainsKey(UhdmVpi.VpiNamedEvent);

        string output = FlattenDesign(mod);
        OutputBudget(output);
        return output;
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static int TypeOf(UhdmNode? h) => h is not null ? h.VpiType : -1;
    private static string NameOf(UhdmNode? h) => h is not null ? h.Name : string.Empty;

    internal static string Q(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u00");
                        sb.Append("0123456789abcdef"[(ch >> 4) & 0xF]);
                        sb.Append("0123456789abcdef"[ch & 0xF]);
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string ConstU32Json(ulong v)
    {
        char[] bits = new char[32];
        for (int i = 0; i < 32; i++)
            bits[i] = ((v >> (31 - i)) & 1UL) != 0 ? '1' : '0';
        return $"{{\"kind\":\"const\",\"bits\":{Q(new string(bits))}}}";
    }

    private static ulong EvalClog2(ulong v)
    {
        if (v <= 1) return 0;
        ulong r = 0, x = v - 1;
        while (x > 0) { x >>= 1; r++; }
        return r;
    }

    private string SignalName(UhdmNode h)
    {
        UhdmNode? current = h, last = h;
        string result = string.Empty;
        bool hadActual = false;

        for (int depth = 0; current is not null && depth < 64; depth++)
        {
            if (flatSymbols.TryGetValue(current, out string? found))
            {
                result = found;
                break;
            }
            if (current.VpiType is not (UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar))
            {
                foreach (var (flat, decl) in flatDeclarations)
                {
                    Need(++declarationAliasChecks <= 1_000_000, "declaration alias budget exceeded");
                    if (SameLocal(current, decl))
                    {
                        Need(result.Length == 0 || result == flat, "ambiguous cloned declaration");
                        result = flat;
                    }
                }
            }
            if (result.Length > 0)
            {
                flatSymbols.TryAdd(current, result);
                break;
            }
            last = current;
            var actual = current.One(UhdmVpi.VpiActual);
            if (actual is not null) hadActual = true;
            if (ReferenceEquals(actual, current)) break;
            current = actual;
        }

        if (result.Length == 0)
        {
            if (hadActual)
            {
                static string Describe(UhdmNode obj) =>
                    $"{obj.FullName} type={obj.VpiType} file={obj.File}:{obj.LineNo}:{obj.ColumnNo}";
                var sb = new StringBuilder($"reference actual is outside collected scopes: {h.Name}; actual {Describe(last!)}");
                foreach (var (flat, decl) in flatDeclarations)
                    if (decl.Name == h.Name) sb.Append($"; candidate {flat} {Describe(decl)}");
                throw new InvalidOperationException(sb.ToString());
            }
            Need(activeSymbols.TryGetValue(h.Name, out string? active), "unresolved scoped reference: " + h.Name);
            result = active!;
        }

        return clockAliases.TryGetValue(result, out string? alias) ? alias : result;
    }

    private static UhdmNode? BaseSpec(UhdmNode? spec, int depth = 0)
    {
        Need(depth < 16, "integral base type depth exceeded");
        if (TypeOf(spec) == UhdmVpi.VpiRefTypespec) return BaseSpec(spec!.One(UhdmVpi.VpiActual), depth + 1);
        if (TypeOf(spec) == UhdmVpi.VpiEnumTypespec) return BaseSpec(spec!.One(UhdmVpi.VpiBaseTypespec), depth + 1);
        return spec;
    }

    private static UhdmNode? WordSpec(UhdmNode? h)
    {
        if (h is null) return null;
        var reference = h.One(h.VpiType == UhdmVpi.VpiIODecl ? UhdmVpi.VpiTypedef : UhdmVpi.VpiTypespec);
        return BaseSpec(reference?.One(UhdmVpi.VpiActual) ?? reference);
    }

    private static int AtomWidth(UhdmNode? spec) => TypeOf(spec) switch
    {
        UhdmVpi.VpiByteTypespec => 8,
        UhdmVpi.VpiShortIntTypespec => 16,
        UhdmVpi.VpiIntTypespec or UhdmVpi.VpiIntegerTypespec => 32,
        UhdmVpi.VpiLongIntTypespec or UhdmVpi.VpiTimeTypespec => 64,
        _ => 0
    };

    private static bool AggregateSpec(UhdmNode? spec) =>
        TypeOf(spec) is UhdmVpi.VpiStructTypespec or UhdmVpi.VpiUnionTypespec;

    private static bool DynamicArray(UhdmNode? h) => TypeOf(h) == UhdmVpi.VpiArrayVar && h!.GetInt(UhdmVpi.VpiArrayType) == UhdmVpi.VpiDynamicArray;
    private static bool QueueArray(UhdmNode? h) => TypeOf(h) == UhdmVpi.VpiArrayVar && h!.GetInt(UhdmVpi.VpiArrayType) == UhdmVpi.VpiQueueArray;
    private static bool AssociativeArray(UhdmNode? h) => TypeOf(h) == UhdmVpi.VpiArrayVar && h!.GetInt(UhdmVpi.VpiArrayType) == UhdmVpi.VpiAssocArray;
    private static bool LifetimeArray(UhdmNode? h) => DynamicArray(h) || QueueArray(h) || AssociativeArray(h);

    private bool TwoStateWord(UhdmNode declaration)
    {
        var spec = WordSpec(declaration);
        int t = TypeOf(spec);
        if (AggregateSpec(spec)) return AggregateState(spec, 0);
        return declaration.VpiType == UhdmVpi.VpiBitVar
            || t is UhdmVpi.VpiBitTypespec or UhdmVpi.VpiByteTypespec or UhdmVpi.VpiShortIntTypespec
                or UhdmVpi.VpiIntTypespec or UhdmVpi.VpiLongIntTypespec;
    }

    private ulong SelectedConstant(UhdmNode? h)
    {
        var orig = OriginalExpression(h);
        if (orig is not null) h = orig;
        Need(TypeOf(h) == UhdmVpi.VpiConstant, "select bounds and replication counts require literal constants");
        return Integer(h);
    }

    internal int Width(UhdmNode? h, int depth = 0)
    {
        Need(h is not null && depth < limits.MaxDepth, "word width depth exceeded");
        var original = OriginalExpression(h);
        if (original is not null) return Width(original, depth + 1);
        var boundDecl = BoundVariableReference(h!);
        if (boundDecl is not null) return Width(boundDecl, depth + 1);
        if (UnsizedDecimal(h)) return Bits(h).Length;
        if (h!.VpiType == UhdmVpi.VpiHierPath) return DynamicSize(h) ? 32 : ResolvePackedField(h).Width;

        if (h.VpiType == UhdmVpi.VpiVarSelect)
        {
            var actual = h.One(UhdmVpi.VpiActual);
            Need(TypeOf(actual) == UhdmVpi.VpiArrayVar, "variable selection requires a fixed array");
            var elements = actual!.Many(UhdmVpi.VpiReg);
            if (elements.Count == 0) elements = actual.Many(UhdmVpi.VpiVariables);
            Need(elements.Count == 1, "one array element declaration required");
            return Width(elements[0], depth + 1);
        }
        if (h.VpiType == UhdmVpi.VpiBitSelect)
        {
            var actual = h.One(UhdmVpi.VpiActual);
            if (TypeOf(actual) != UhdmVpi.VpiArrayVar) return 1;
            var elements = actual!.Many(UhdmVpi.VpiReg);
            if (elements.Count == 0) elements = actual.Many(UhdmVpi.VpiVariables);
            Need(elements.Count == 1, "one memory element declaration required");
            return Width(elements[0], depth + 1);
        }
        if (h.VpiType == UhdmVpi.VpiIndexedPartSelect)
        {
            ulong n = SelectedConstant(h.One(UhdmVpi.VpiWidthExpr));
            Need(n >= 1 && n <= (ulong)limits.MaxWordBits, "indexed select width exceeds limit");
            return (int)n;
        }
        if (h.VpiType == UhdmVpi.VpiPartSelect)
        {
            ulong l = SelectedConstant(h.One(UhdmVpi.VpiLeftRange));
            ulong r = SelectedConstant(h.One(UhdmVpi.VpiRightRange));
            Need(l >= r && l - r < (ulong)limits.MaxWordBits, "unsupported constant part-select width");
            return (int)(l - r + 1);
        }
        if (h.VpiType == UhdmVpi.VpiOperation)
        {
            int op = (int)h.GetInt(UhdmVpi.VpiOpType);
            var args = h.Many(UhdmVpi.VpiOperand);
            if (op == UhdmVpi.VpiConcatOp)
            {
                Need(args.Count > 0 && args.Count <= limits.MaxWordBits, "concat operand limit exceeded");
                int n = 0;
                foreach (var a in args)
                {
                    n += Width(a, depth + 1);
                    Need(n <= limits.MaxWordBits, "concat exceeds word width limit");
                }
                return n;
            }
            if (op == UhdmVpi.VpiMultiConcatOp)
            {
                Need(args.Count == 2, "replication requires count and expression");
                ulong count = SelectedConstant(args[0]);
                int n = Width(args[1], depth + 1);
                Need(count >= 1 && count <= (ulong)limits.MaxWordBits && count * (ulong)n <= (ulong)limits.MaxWordBits,
                    "replication exceeds word width limit");
                return (int)(count * (ulong)n);
            }
            if (op == UhdmVpi.VpiNotOp || op == UhdmVpi.VpiLogAndOp || op == UhdmVpi.VpiLogOrOp ||
                (op >= UhdmVpi.VpiUnaryAndOp && op <= UhdmVpi.VpiUnaryXNorOp) ||
                (op >= UhdmVpi.VpiEqOp && op <= UhdmVpi.VpiLeOp) ||
                op == UhdmVpi.VpiWildEqOp || op == UhdmVpi.VpiWildNeqOp || op == UhdmVpi.VpiInsideOp)
                return 1;
            Need(args.Count > 0, "expression width requires operands");
            if (op is UhdmVpi.VpiLShiftOp or UhdmVpi.VpiRShiftOp or UhdmVpi.VpiArithRShiftOp)
                return Width(args[0], depth + 1);
            int start = op == UhdmVpi.VpiConditionOp ? 1 : 0;
            Need(start < args.Count, "conditional width requires branches");
            int maxW = 0;
            for (int i = start; i < args.Count; i++)
                maxW = Math.Max(maxW, Width(args[i], depth + 1));
            return maxW;
        }
        if (h.VpiType == UhdmVpi.VpiFuncCall)
            return Width(h.One(UhdmVpi.VpiFunction)?.One(UhdmVpi.VpiReturn), depth + 1);
        if (h.VpiType == UhdmVpi.VpiConstant)
            return Bits(h).Length;
        if (h.VpiType == UhdmVpi.VpiSysFuncCall)
        {
            var args = h.Many(UhdmVpi.VpiArgument);
            string fn = h.Name;
            if (fn is "$bits" or "$clog2" or "$countones" or "$test$plusargs" && args.Count == 1) return 32;
            if (fn is "$onehot" or "$onehot0" or "$isunknown" && args.Count == 1) return 1;
            if (fn is "$urandom" or "$random" && args.Count == 0) return 32;
            if (fn == "$urandom_range" && args.Count is 1 or 2) return 32;
            Need(fn is "$signed" or "$unsigned" && args.Count == 1, "unsupported system function width");
            return Width(args[0], depth + 1);
        }

        var actualNode = h.One(UhdmVpi.VpiActual);
        if (IsParameterReference(h)) return ParameterReferenceWidth(h);
        if (actualNode is not null && !ReferenceEquals(actualNode, h)) return Width(actualNode, depth + 1);

        var spec = WordSpec(h);
        if (AggregateSpec(spec)) return AggregateWidth(spec, depth + 1);
        int atomW = AtomWidth(spec);
        if (atomW != 0) return atomW;
        Need(TypeOf(spec) is UhdmVpi.VpiLogicTypespec or UhdmVpi.VpiBitTypespec, "only logic word widths supported: " + h.Name);
        var ranges = spec!.Many(UhdmVpi.VpiRange);
        Need(ranges.Count <= 1, "multidimensional packed words unsupported");
        if (ranges.Count == 0) return 1;
        ulong left = Integer(ranges[0].One(UhdmVpi.VpiLeftRange));
        ulong right = Integer(ranges[0].One(UhdmVpi.VpiRightRange));
        Need(left >= right && left - right < (ulong)limits.MaxWordBits, "word width exceeds limit");
        return (int)(left - right + 1);
    }

    private static bool DeclaredSigned(UhdmNode h)
    {
        var spec = WordSpec(h);
        return h.GetBool(UhdmVpi.VpiSigned) || (spec is not null && spec.GetBool(UhdmVpi.VpiSigned));
    }

    private void LogicWord(UhdmNode h)
    {
        var spec = WordSpec(h);
        if (AtomWidth(spec) != 0) return;
        if (AggregateSpec(spec))
        {
            AggregateWidth(spec, 0);
            AggregateState(spec, 0);
            return;
        }
        Need(TypeOf(spec) is UhdmVpi.VpiLogicTypespec or UhdmVpi.VpiBitTypespec, "only packed logic/bit words supported");
        var ranges = spec!.Many(UhdmVpi.VpiRange);
        Need(ranges.Count <= 1, "multidimensional packed words unsupported");
        if (ranges.Count > 0)
            Need(Integer(ranges[0].One(UhdmVpi.VpiRightRange)) == 0 && Integer(ranges[0].One(UhdmVpi.VpiLeftRange)) == (ulong)(Width(h) - 1),
                "packed words require descending zero-based bounds [width-1:0]");
    }

    private ulong Integer(UhdmNode? h, int depth = 0)
    {
        Need(h is not null && depth < 64, "constant integer depth exceeded");
        if (h!.VpiType == UhdmVpi.VpiOperation)
        {
            var a = h.Many(UhdmVpi.VpiOperand);
            Need(a.Count == 2, "range expression unsupported");
            ulong l = Integer(a[0], depth + 1), r = Integer(a[1], depth + 1);
            long op = h.GetInt(UhdmVpi.VpiOpType);
            Need(op == UhdmVpi.VpiAddOp || op == UhdmVpi.VpiSubOp, "range operation unsupported");
            return op == UhdmVpi.VpiAddOp ? l + r : l - r;
        }
        Need(h.VpiType == UhdmVpi.VpiConstant, "constant range required");
        string s = Bits(h);
        Need(!s.Any(c => c is not ('0' or '1')), "unknown array bound");
        Need(!(SignedExpression(h, 0) && s[0] == '1'), "negative signed bounds and replication counts unsupported");
        return Convert.ToUInt64(s, 2);
    }

    private static string ConcatTree(List<string> values, int begin, int end)
    {
        if (end - begin == 1) return values[begin];
        int mid = begin + (end - begin) / 2;
        return $"{{\"kind\":\"concat\",\"args\":[{ConcatTree(values, begin, mid)},{ConcatTree(values, mid, end)}]}}";
    }

    private void OutputBudget(string value)
    {
        Need(value.Length <= limits.MaxJsonBytes, "normalized RTL exceeds byte limit");
        int nesting = 0, objects = 0;
        bool quoted = false, escaped = false;
        foreach (char c in value)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c is '{' or '[')
            {
                Need(++nesting <= limits.MaxDepth, "normalized RTL JSON depth exceeded");
                if (c == '{') Need(++objects <= limits.MaxNodes, "normalized RTL node budget exceeded");
            }
            else if (c is '}' or ']') nesting--;
        }
    }

    private bool SignedExpression(UhdmNode? h, int depth = 0)
    {
        Need(h is not null && depth < limits.MaxDepth, "signedness depth exceeded");
        if (h!.VpiType == UhdmVpi.VpiConstant)
        {
            var spec = WordSpec(h);
            string literal = h.GetString(UhdmVpi.VpiDecompile);
            bool allDec = literal.Length > 0 && literal.All(c => c is (>= '0' and <= '9') or '_');
            return allDec || h.GetBool(UhdmVpi.VpiSigned) || (spec is not null && spec.GetBool(UhdmVpi.VpiSigned))
                || h.GetInt(UhdmVpi.VpiConstType) == UhdmVpi.VpiIntConst
                || literal.Contains("'s", StringComparison.Ordinal) || literal.Contains("'S", StringComparison.Ordinal);
        }
        if (h.VpiType == UhdmVpi.VpiSysFuncCall && h.Name == "$random") return true;
        if (IsParameterReference(h)) return ParameterReferenceSigned(h);
        var decl = BoundVariableReference(h);
        if (decl is not null) return DeclaredSigned(decl);
        if (h.VpiType != UhdmVpi.VpiOperation) return false;
        int op = (int)h.GetInt(UhdmVpi.VpiOpType);
        var a = h.Many(UhdmVpi.VpiOperand);
        if (op == UhdmVpi.VpiConcatOp || op == UhdmVpi.VpiNotOp || op == UhdmVpi.VpiLogAndOp || op == UhdmVpi.VpiLogOrOp ||
            (op >= UhdmVpi.VpiUnaryAndOp && op <= UhdmVpi.VpiUnaryXNorOp) ||
            (op >= UhdmVpi.VpiEqOp && op <= UhdmVpi.VpiLeOp) ||
            op == UhdmVpi.VpiWildEqOp || op == UhdmVpi.VpiWildNeqOp || op == UhdmVpi.VpiInsideOp)
            return false;
        if (op == UhdmVpi.VpiConditionOp) return a.Count == 3 && SignedExpression(a[1], depth + 1) && SignedExpression(a[2], depth + 1);
        if (op is UhdmVpi.VpiLShiftOp or UhdmVpi.VpiRShiftOp or UhdmVpi.VpiArithLShiftOp or UhdmVpi.VpiArithRShiftOp)
            return a.Count > 0 && SignedExpression(a[0], depth + 1);
        foreach (var operand in a)
            if (!SignedExpression(operand, depth + 1)) return false;
        return a.Count > 0;
    }

    private int flatDeclsIndexedCount = -1;

    private UhdmNode? BoundVariableReference(UhdmNode h)
    {
        if (insideCall || h.VpiType is not (UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar)) return null;
        var actual = h.One(UhdmVpi.VpiActual);
        if (actual is not null && flatSymbols.ContainsKey(actual)) return null;
        if (!syntaxVariableReferences.TryGetValue(LexicalKey(h), out var source)) return null;
        Need(source is not null, "ambiguous original variable reference");
        if (flatDeclsIndexedCount != flatDeclarations.Count)
        {
            flatDeclsByCanonicalKey.Clear();
            foreach (var (_, decl) in flatDeclarations)
            {
                string key = CanonicalKey(decl);
                if (!flatDeclsByCanonicalKey.TryGetValue(key, out var list))
                {
                    list = [];
                    flatDeclsByCanonicalKey[key] = list;
                }
                list.Add(decl);
            }
            flatDeclsIndexedCount = flatDeclarations.Count;
        }
        string expected = CanonicalKey(source!);
        if (!flatDeclsByCanonicalKey.TryGetValue(expected, out var candidates)) return null;
        foreach (var scope in parameterVisibleScopes)
        {
            UhdmNode? result = null;
            foreach (var decl in candidates)
            {
                Need(++syntaxSymbolChecks <= 1_000_000, "original variable binding budget exceeded");
                if (OwnedParameter(decl, scope))
                {
                    Need(result is null || ReferenceEquals(result, decl), "ambiguous scoped original variable");
                    result = decl;
                }
            }
            if (result is not null) return result;
        }
        return null;
    }

    private bool DynamicSize(UhdmNode h)
    {
        if (h.VpiType != UhdmVpi.VpiHierPath) return false;
        var elements = h.GetFieldList("Path_elems");
        if (elements.Count == 0) return false;
        var receiver = elements[0];
        var actual = receiver.One(UhdmVpi.VpiActual);
        if (!LifetimeArray(actual)) return false;
        Need(!insideCall && elements.Count == 2, "dynamic array methods require a whole module-scope receiver");
        var method = elements[1];
        var args = method.Many(UhdmVpi.VpiArgument);
        string n = method.Name;
        Need(method.VpiType == UhdmVpi.VpiMethodFuncCall &&
            ((args.Count == 0 && (n == "size" || (AssociativeArray(actual) && n == "num"))) ||
             (AssociativeArray(actual) && n == "exists" && args.Count == 1)),
            "container expression supports size/num/exists; side effects require ordered statements");
        Need(flatSymbols.ContainsKey(actual!), "dynamic method receiver must bind a collected declaration");
        return true;
    }

    private string DynamicSizeExpression(UhdmNode h)
    {
        var elements = h.GetFieldList("Path_elems");
        var receiver = elements[0];
        var method = elements[1];
        bool exists = method.Name == "exists";
        return $"{{\"kind\":{Q(exists ? "exists" : "size")},\"name\":{Q(SignalName(receiver))}{(exists ? ",\"index\":" + Expr(method.Many(UhdmVpi.VpiArgument)[0]) : "")}}}";
    }

    private static readonly Dictionary<int, string> OpNames = new()
    {
        [UhdmVpi.VpiAddOp] = "add", [UhdmVpi.VpiSubOp] = "sub", [UhdmVpi.VpiLogAndOp] = "and",
        [UhdmVpi.VpiNotOp] = "not", [UhdmVpi.VpiEqOp] = "eq", [UhdmVpi.VpiConcatOp] = "concat",
        [UhdmVpi.VpiBitAndOp] = "bitand", [UhdmVpi.VpiBitOrOp] = "bitor", [UhdmVpi.VpiBitXorOp] = "bitxor",
        [UhdmVpi.VpiBitNegOp] = "bitnot", [UhdmVpi.VpiLogOrOp] = "or", [UhdmVpi.VpiNeqOp] = "neq",
        [UhdmVpi.VpiCaseEqOp] = "caseeq", [UhdmVpi.VpiCaseNeqOp] = "caseneq",
        [UhdmVpi.VpiWildEqOp] = "wildeq", [UhdmVpi.VpiWildNeqOp] = "wildneq",
        [UhdmVpi.VpiLtOp] = "lt", [UhdmVpi.VpiLeOp] = "le", [UhdmVpi.VpiGtOp] = "gt", [UhdmVpi.VpiGeOp] = "ge",
        [UhdmVpi.VpiBitXNorOp] = "bitxnor", [UhdmVpi.VpiMinusOp] = "neg",
        [UhdmVpi.VpiLShiftOp] = "shl", [UhdmVpi.VpiRShiftOp] = "shr", [UhdmVpi.VpiArithRShiftOp] = "ashr",
        [UhdmVpi.VpiUnaryAndOp] = "redand", [UhdmVpi.VpiUnaryNandOp] = "rednand",
        [UhdmVpi.VpiUnaryOrOp] = "redor", [UhdmVpi.VpiUnaryNorOp] = "rednor",
        [UhdmVpi.VpiUnaryXorOp] = "redxor", [UhdmVpi.VpiUnaryXNorOp] = "redxnor",
        [UhdmVpi.VpiPlusOp] = "pos", [UhdmVpi.VpiMultOp] = "mul", [UhdmVpi.VpiDivOp] = "div",
        [UhdmVpi.VpiModOp] = "mod", [UhdmVpi.VpiConditionOp] = "cond",
    };

    private string ExprCore(UhdmNode? h, int depth)
    {
        Need(h is not null && depth < limits.MaxDepth && ++nodes <= limits.MaxNodes,
            "expression missing or resource limit exceeded");
        Need(!syntaxParameterSelections.Contains(LexicalKey(h)),
            "parameter selections unsupported; pass the whole parameter");
        var original = OriginalExpression(h);
        if (original is not null) return Expr(original, depth + 1);

        var enumerator = h!.VpiType == UhdmVpi.VpiEnumConst ? h : h.One(UhdmVpi.VpiActual);
        if (TypeOf(enumerator) == UhdmVpi.VpiEnumConst)
        {
            long sz = enumerator!.GetInt(UhdmVpi.VpiSize);
            Need(sz >= 1 && sz <= limits.MaxWordBits, "enumerator width unsupported");
            var temp = db.Create(UhdmObjectType.Constant)
                .SetInt("VpiSize", sz)
                .SetString("VpiValue", enumerator.GetFieldString("VpiValue"));
            var spec = BaseSpec(enumerator.Parent);
            return $"{{\"kind\":\"const\",\"bits\":{Q(ConstantBits(temp))}{(spec is not null && spec.GetBool(UhdmVpi.VpiSigned) ? ",\"signed\":true" : "")}}}";
        }

        if (IsParameterReference(h)) return ParameterExpr(h);
        var boundDecl = BoundVariableReference(h);
        if (boundDecl is not null) return $"{{\"kind\":\"ref\",\"name\":{Q(SignalName(boundDecl))}}}";

        if (h.VpiType == UhdmVpi.VpiConstant)
        {
            string b = Bits(h);
            string literal = h.GetString(UhdmVpi.VpiDecompile);
            Need(literal is not ("'1" or "'x" or "'z" or "'X" or "'Z"), "unbased fill literal unsupported except zero");
            return $"{{\"kind\":\"const\",\"bits\":{Q(b)}{(SignedExpression(h) ? ",\"signed\":true" : "")}}}";
        }
        if (h.VpiType == UhdmVpi.VpiVarSelect)
        {
            Need(!insideCall, "captured global memory reads unsupported");
            var actual = h.One(UhdmVpi.VpiActual);
            Need(TypeOf(actual) == UhdmVpi.VpiArrayVar, "variable selection requires a fixed array");
            var indices = h.Many(UhdmVpi.VpiIndex);
            var ranges = actual!.Many(UhdmVpi.VpiRange);
            Need(indices.Count == ranges.Count && indices.Count is >= 2 and <= 4, "array index rank mismatch");
            var sb = new StringBuilder($"{{\"kind\":\"index\",\"name\":{Q(SignalName(h))},\"indices\":[");
            for (int i = 0; i < indices.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Expr(indices[i], depth + 1));
            }
            return sb.Append("]}").ToString();
        }
        if (h.VpiType == UhdmVpi.VpiBitSelect)
        {
            var index = h.One(UhdmVpi.VpiIndex);
            var actual = h.One(UhdmVpi.VpiActual);
            if (TypeOf(index) == UhdmVpi.VpiConstant && !AssociativeArray(actual))
                Need(!(SignedExpression(index) && Bits(index)[0] == '1'), "negative constant packed indices unsupported");
            Need(TypeOf(actual) != UhdmVpi.VpiParameter && !IsParameterReference(actual),
                "parameter selections unsupported; pass the whole parameter");
            if (TypeOf(actual) == UhdmVpi.VpiArrayVar)
            {
                Need(!insideCall, "captured global memory reads unsupported");
                return $"{{\"kind\":\"index\",\"name\":{Q(SignalName(h))},\"index\":{Expr(index, depth + 1)}}}";
            }
            Need(actual is not null && actual.VpiType is not (UhdmVpi.VpiBitSelect or UhdmVpi.VpiPartSelect),
                "packed select requires a declared word");
            return $"{{\"kind\":\"bitselect\",\"value\":{Expr(actual, depth + 1)},\"index\":{Expr(index, depth + 1)}}}";
        }
        if (h.VpiType == UhdmVpi.VpiIndexedPartSelect)
        {
            var index = h.One(UhdmVpi.VpiBaseExpr);
            if (TypeOf(index) == UhdmVpi.VpiConstant)
                Need(!(SignedExpression(index) && Bits(index)[0] == '1'), "negative constant indexed bases unsupported");
            var actual = h.One(UhdmVpi.VpiActual);
            Need(TypeOf(actual) != UhdmVpi.VpiParameter && !IsParameterReference(actual),
                "parameter selections unsupported; pass the whole parameter");
            Need(actual is not null && actual.VpiType is not (UhdmVpi.VpiArrayVar or UhdmVpi.VpiBitSelect or UhdmVpi.VpiPartSelect or UhdmVpi.VpiIndexedPartSelect),
                "indexed select requires a declared packed word");
            ulong n = SelectedConstant(h.One(UhdmVpi.VpiWidthExpr));
            Need(n >= 1 && n <= (ulong)limits.MaxWordBits, "indexed select width exceeds limit");
            long dir = h.GetInt(UhdmVpi.VpiIndexedPartSelectType);
            Need(dir == UhdmVpi.VpiPosIndexed || dir == UhdmVpi.VpiNegIndexed, "unsupported indexed select direction");
            return $"{{\"kind\":\"indexedselect\",\"value\":{Expr(actual, depth + 1)},\"base\":{Expr(index, depth + 1)},\"width\":{n},\"descending\":{(dir == UhdmVpi.VpiNegIndexed ? "true" : "false")}}}";
        }
        if (h.VpiType == UhdmVpi.VpiPartSelect)
        {
            var actual = h.One(UhdmVpi.VpiActual);
            Need(TypeOf(actual) != UhdmVpi.VpiParameter && !IsParameterReference(actual),
                "parameter selections unsupported; pass the whole parameter");
            Need(actual is not null && actual.VpiType != UhdmVpi.VpiArrayVar, "part select requires a packed word");
            ulong left = SelectedConstant(h.One(UhdmVpi.VpiLeftRange));
            ulong right = SelectedConstant(h.One(UhdmVpi.VpiRightRange));
            Need(left <= int.MaxValue && right <= left && left - right < (ulong)limits.MaxWordBits,
                "part select requires nonnegative descending constant bounds");
            return $"{{\"kind\":\"partselect\",\"value\":{Expr(actual, depth + 1)},\"left\":{left},\"right\":{right}}}";
        }
        if (h.VpiType == UhdmVpi.VpiHierPath)
            return DynamicSize(h) ? DynamicSizeExpression(h) : PackedFieldExpression(h, false);
        if (h.VpiType == UhdmVpi.VpiFuncCall)
            return LowerFunction(h, depth + 1);
        if (h.VpiType == UhdmVpi.VpiSysFuncCall)
        {
            var args = h.Many(UhdmVpi.VpiArgument);
            string fn = h.Name;
            if (fn == "$bits" && args.Count == 1) return ConstU32Json((ulong)Width(args[0], depth + 1));
            if (fn == "$clog2" && args.Count == 1)
            {
                var src = OriginalExpression(args[0]) ?? args[0];
                if (src.VpiType == UhdmVpi.VpiConstant) return ConstU32Json(EvalClog2(Integer(src)));
                return $"{{\"kind\":\"clog2\",\"args\":[{Expr(args[0], depth + 1)}]}}";
            }
            if (fn is "$countones" or "$onehot" or "$onehot0" or "$isunknown" && args.Count == 1)
                return $"{{\"kind\":{Q(fn[1..])},\"args\":[{Expr(args[0], depth + 1)}]}}";
            if (fn == "$test$plusargs" && args.Count == 1)
            {
                string raw = args[0].GetString(UhdmVpi.VpiDecompile);
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1];
                Need(raw.Length > 0, "$test$plusargs requires a non-empty string literal");
                return $"{{\"kind\":\"test_plusargs\",\"prefix\":{Q(raw)}}}";
            }
            if (fn == "$urandom" && args.Count == 0) return "{\"kind\":\"urandom\"}";
            if (fn == "$random" && args.Count == 0) return "{\"kind\":\"random\"}";
            if (fn == "$urandom_range" && args.Count is 1 or 2)
                return $"{{\"kind\":\"urandom_range\",\"max\":{Expr(args[0], depth + 1)}{(args.Count == 2 ? ",\"min\":" + Expr(args[1], depth + 1) : "")}}}";
            Need(fn is "$signed" or "$unsigned" && args.Count == 1, "unsupported system function");
            return $"{{\"kind\":{Q(fn == "$signed" ? "castsigned" : "castunsigned")},\"args\":[{Expr(args[0], depth + 1)}]}}";
        }
        if (h.VpiType == UhdmVpi.VpiOperation)
        {
            int op = (int)h.GetInt(UhdmVpi.VpiOpType);
            var ops = h.Many(UhdmVpi.VpiOperand);
            if (op == UhdmVpi.VpiInsideOp)
            {
                Need(ops.Count is >= 2 and <= 257, "inside requires lhs and 1..256 set items");
                var sb = new StringBuilder($"{{\"kind\":\"inside\",\"value\":{Expr(ops[0], depth + 1)},\"items\":[");
                for (int i = 1; i < ops.Count; i++)
                {
                    if (i > 1) sb.Append(',');
                    sb.Append(Expr(ops[i], depth + 1));
                }
                return sb.Append("]}").ToString();
            }
            if (op is UhdmVpi.VpiConcatOp or UhdmVpi.VpiMultiConcatOp)
            {
                var values = new List<string>();
                if (op == UhdmVpi.VpiMultiConcatOp)
                {
                    Need(ops.Count == 2, "replication requires count and expression");
                    ulong count = SelectedConstant(ops[0]);
                    Need(count >= 1 && count <= (ulong)limits.MaxWordBits && count * (ulong)Width(ops[1]) <= (ulong)limits.MaxWordBits,
                        "replication exceeds word width limit");
                    string v = Expr(ops[1], depth + 1);
                    Need((ulong)v.Length * count + count * 32 <= (ulong)limits.MaxJsonBytes, "replication exceeds normalized output budget");
                    for (ulong i = 0; i < count; i++) values.Add(v);
                }
                else
                {
                    Need(ops.Count > 0 && ops.Count <= limits.MaxWordBits, "concat operand limit exceeded");
                    int total = 0, expanded = 0;
                    foreach (var operand in ops)
                    {
                        total += Width(operand);
                        Need(total <= limits.MaxWordBits, "concat exceeds word width limit");
                        string v = Expr(operand, depth + 1);
                        expanded += v.Length;
                        Need(expanded <= limits.MaxJsonBytes, "concat expanded byte budget exceeded");
                        values.Add(v);
                    }
                }
                string tree = ConcatTree(values, 0, values.Count);
                OutputBudget(tree);
                return tree;
            }
            Need(OpNames.TryGetValue(op, out string? opName), "unsupported expression operation " + op);
            bool unary = opName is "not" or "bitnot" or "neg" or "pos" || opName!.StartsWith("red", StringComparison.Ordinal);
            Need(ops.Count == (unary ? 1 : opName == "cond" ? 3 : 2), "unsupported expression arity");
            var exprSb = new StringBuilder($"{{\"kind\":{Q(opName!)},\"args\":[");
            for (int i = 0; i < ops.Count; i++)
            {
                if (i > 0) exprSb.Append(',');
                exprSb.Append(Expr(ops[i], depth + 1));
            }
            return exprSb.Append("]}").ToString();
        }

        Need(h.Name.Length > 0 &&
            (h.VpiType is UhdmVpi.VpiReg or UhdmVpi.VpiNet or UhdmVpi.VpiBitVar or UhdmVpi.VpiLogicVar
                or UhdmVpi.VpiRefObj or UhdmVpi.VpiVariables or UhdmVpi.VpiIntVar or UhdmVpi.VpiByteVar
                or UhdmVpi.VpiShortIntVar or UhdmVpi.VpiLongIntVar or UhdmVpi.VpiIntegerVar or UhdmVpi.VpiTimeVar
                or UhdmVpi.VpiEnumVar or UhdmVpi.VpiRefVar
                || (insideCall && h.VpiType == UhdmVpi.VpiIODecl)
                || (!insideCall && flatSymbols.ContainsKey(h))),
            "unsupported reference type " + h.VpiType);

        if (insideCall)
        {
            string key = LocalKey(h);
            Need(key.Length > 0, "captured global subroutine reads unsupported");
            var local = callLocals[key];
            Need(local.Assigned, "subroutine local read before assignment");
            return local.Value;
        }
        return $"{{\"kind\":\"ref\",\"name\":{Q(SignalName(h))}}}";
    }

    private string Expr(UhdmNode? h, int depth = 0)
    {
        string value = ExprCore(h, depth);
        Need(value.Length <= limits.MaxJsonBytes, "normalized RTL exceeds byte limit");
        if (depth == 0 || value.Length > limits.MaxJsonBytes / 2)
            OutputBudget(value);
        return value;
    }

    private void TargetShape(UhdmNode? h, int depth = 0)
    {
        Need(h is not null && depth < 64, "assignment target depth exceeded");
        if (h!.VpiType == UhdmVpi.VpiOperation)
        {
            Need(h.GetInt(UhdmVpi.VpiOpType) == UhdmVpi.VpiConcatOp,
                "only concatenation operations supported in assignment targets");
            foreach (var child in h.Many(UhdmVpi.VpiOperand))
                TargetShape(child, depth + 1);
        }
    }

    private string TimedEvents(UhdmNode? h, int depth = 0)
    {
        Need(h is not null && depth < 32, "timed event expression depth");
        if (h!.VpiType == UhdmVpi.VpiOperation)
        {
            long op = h.GetInt(UhdmVpi.VpiOpType);
            var operands = h.Many(UhdmVpi.VpiOperand);
            if (op == UhdmVpi.VpiEventOrOp)
                return string.Join(',', operands.Select(o => TimedEvents(o, depth + 1)));
            Need((op == UhdmVpi.VpiPosedgeOp || op == UhdmVpi.VpiNegedgeOp) && operands.Count == 1,
                "unsupported timed event expression");
            Need(Width(operands[0]) == 1, "timed edge requires scalar");
            return $"{{\"signal\":{Q(SignalName(operands[0]))},\"edge\":{Q(op == UhdmVpi.VpiPosedgeOp ? "posedge" : "negedge")}}}";
        }
        var decl = h.One(UhdmVpi.VpiActual);
        string flat = SignalName(decl ?? h);
        if (flatNamedEvents.Contains(flat)) return $"{{\"event\":{Q(flat)}}}";
        Need(h.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar, "timed level event requires a declared signal");
        return $"{{\"signal\":{Q(flat)},\"edge\":\"change\"}}";
    }

    private ulong DelayScale()
    {
        int unit = (int)mod!.GetFieldInt("VpiTimeUnit");
        Need(unit >= timedPrecision && unit <= 0 && timedPrecision >= -15, "unsupported HDL time scale");
        ulong scale = 1;
        for (int power = timedPrecision; power < unit; power++) scale *= 10;
        return scale;
    }

    private string DelayFields(UhdmNode h)
    {
        Need(h.VpiType == UhdmVpi.VpiDelayControl, "typed delay control required");
        var amount = h.GetFieldRef("Delay");
        string encoded = h.GetFieldString("VpiDelay");
        if ((amount is null || amount.GetInt(UhdmVpi.VpiConstType) == UhdmVpi.VpiRealConst) &&
            encoded.Length > 1 && encoded[0] == '#' && encoded.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            return $"\"literal\":{Q(encoded[1..])},\"unit\":{mod!.GetFieldInt("VpiTimeUnit")},\"precision\":{mod.GetFieldInt("VpiTimePrecision")}";
        }
        if (amount is not null && amount.VpiType != UhdmVpi.VpiConstant)
        {
            Need(amount.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar or UhdmVpi.VpiOperation,
                "unsupported typed delay expression");
            return $"\"amount\":{Expr(amount)},\"scale\":{DelayScale()}";
        }
        ulong ticks;
        if (amount is not null && amount.VpiType == UhdmVpi.VpiConstant)
        {
            ticks = SelectedConstant(amount);
        }
        else
        {
            Need(encoded.Length > 1 && encoded[0] == '#', "typed integer delay required");
            Need(ulong.TryParse(encoded.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out ticks),
                "only integer #N delay serialization supported");
        }
        ulong scale = DelayScale();
        Need(ticks <= ulong.MaxValue / scale, "HDL delay conversion overflow");
        return $"\"ticks\":{ticks * scale}";
    }

    private string StmtImpl(UhdmNode? h, int depth, bool control)
    {
        Need(h is not null && depth < limits.MaxDepth && ++nodes <= limits.MaxNodes,
            "statement missing or resource limit exceeded");

        if (timedLowering && h!.VpiType == UhdmVpi.VpiEventStmt)
        {
            bool qualified = false;
            foreach (var attr in mod!.Many(UhdmVpi.VpiAttribute))
                qualified |= attr.Name == "velosim frontend named triggers v1";
            Need(qualified, "named trigger lowering requires preserved blocking metadata; use the named-trigger frontend overlay");
            var target = h.GetFieldRef("Named_event");
            string flat = SignalName(target ?? h);
            Need(flatNamedEvents.Contains(flat), "named trigger outside collected scopes");
            return $"{{\"kind\":\"trigger\",\"name\":{Q(flat)},\"nonblocking\":{(h.GetFieldBool("VpiBlocking") ? "false" : "true")}}}";
        }
        if (timedLowering && h!.VpiType == UhdmVpi.VpiNullStmt)
            return "{\"kind\":\"block\",\"items\":[]}";
        if (timedLowering && h!.VpiType == UhdmVpi.VpiFork)
        {
            long join = h.GetFieldInt("VpiJoinType");
            Need(join is UhdmVpi.VpiJoin or UhdmVpi.VpiJoinAny or UhdmVpi.VpiJoinNone, "unsupported fork join mode");
            string mode = join == UhdmVpi.VpiJoin ? "all" : join == UhdmVpi.VpiJoinAny ? "any" : "none";
            var sb = new StringBuilder($"{{\"kind\":\"fork\",\"join\":{Q(mode)},\"items\":[");
            bool first = true;
            foreach (var child in h.Many(UhdmVpi.VpiStmt))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Stmt(child, depth + 1));
            }
            return sb.Append("]}").ToString();
        }
        if (timedLowering && h!.VpiType is UhdmVpi.VpiWaitFork or UhdmVpi.VpiDisableFork)
            return $"{{\"kind\":{Q(h.VpiType == UhdmVpi.VpiWaitFork ? "wait_fork" : "disable_fork")}}}";
        if (timedLowering && h!.VpiType is UhdmVpi.VpiBreak or UhdmVpi.VpiContinue)
            return $"{{\"kind\":{Q(h.VpiType == UhdmVpi.VpiBreak ? "break" : "continue")}}}";
        if (timedLowering && h!.VpiType == UhdmVpi.VpiDelayControl)
        {
            var body = h.One(UhdmVpi.VpiStmt);
            return $"{{\"kind\":\"delay\",{DelayFields(h)},\"body\":{(body is not null ? Stmt(body, depth + 1) : "{\"kind\":\"block\",\"items\":[]}")}}}";
        }
        if (timedLowering && h!.VpiType == UhdmVpi.VpiEventControl)
        {
            var body = h.One(UhdmVpi.VpiStmt);
            return $"{{\"kind\":\"event\",\"events\":[{TimedEvents(h.One(UhdmVpi.VpiCondition))}],\"body\":{(body is not null ? Stmt(body, depth + 1) : "{\"kind\":\"block\",\"items\":[]}")}}}";
        }
        if (timedLowering && h!.VpiType == UhdmVpi.VpiWait)
            return $"{{\"kind\":\"wait\",\"condition\":{Expr(h.One(UhdmVpi.VpiCondition))},\"body\":{Stmt(h.One(UhdmVpi.VpiStmt), depth + 1)}}}";

        if (h!.VpiType == UhdmVpi.VpiSysTaskCall)
        {
            string fn = h.Name;
            var args = h.Many(UhdmVpi.VpiArgument);
            if (fn == "$finish") return "{\"kind\":\"finish\"}";
            if (fn == "$fatal")
            {
                int code = 1;
                string fmt = string.Empty;
                if (args.Count > 0 && args[0].VpiType == UhdmVpi.VpiConstant) code = (int)Integer(args[0]);
                if (args.Count >= 2 && args[1].VpiType == UhdmVpi.VpiConstant)
                {
                    string raw = args[1].GetFieldString("VpiValue");
                    int colon = raw.IndexOf(':');
                    if (colon >= 0 && raw[..colon] == "STRING") fmt = raw[(colon + 1)..];
                }
                var sb = new StringBuilder($"{{\"kind\":\"fatal\",\"code\":{code}");
                if (fmt.Length > 0)
                {
                    sb.Append(",\"format\":").Append(Q(fmt));
                    if (args.Count > 2)
                    {
                        sb.Append(",\"args\":[");
                        for (int i = 2; i < args.Count; i++)
                        {
                            if (i > 2) sb.Append(',');
                            sb.Append(Expr(args[i], depth + 1));
                        }
                        sb.Append(']');
                    }
                }
                return sb.Append('}').ToString();
            }
            if ((fn == "$display" || fn == "$write") && args.Count > 0 && args[0].VpiType == UhdmVpi.VpiConstant)
            {
                string raw = args[0].GetFieldString("VpiValue");
                int colon = raw.IndexOf(':');
                Need(colon >= 0 && raw[..colon] == "STRING", "display format must be a string literal");
                var sb = new StringBuilder($"{{\"kind\":\"display\",\"format\":{Q(raw[(colon + 1)..])},\"newline\":{(fn == "$display" ? "true" : "false")},\"args\":[");
                for (int i = 1; i < args.Count; i++)
                {
                    if (i > 1) sb.Append(',');
                    sb.Append(Expr(args[i], depth + 1));
                }
                return sb.Append("]}").ToString();
            }
        }

        if (h.VpiType is UhdmVpi.VpiMethodFuncCall or UhdmVpi.VpiMethodTaskCall)
        {
            var receiver = h.One(UhdmVpi.VpiPrefix);
            var actual = receiver?.One(UhdmVpi.VpiActual);
            var args = h.Many(UhdmVpi.VpiArgument);
            string method = h.Name;
            Need(!insideCall && LifetimeArray(actual) && flatSymbols.ContainsKey(actual!),
                "container methods require a collected module-scope receiver");
            if (method == "delete")
            {
                Need(args.Count == 0 || ((QueueArray(actual) || AssociativeArray(actual)) && args.Count == 1),
                    "delete accepts an optional index only for queues/associative arrays");
                return $"{{\"kind\":\"delete\",\"name\":{Q(SignalName(receiver!))}{(args.Count == 0 ? "" : ",\"index\":" + Expr(args[0]))}}}";
            }
            if (AssociativeArray(actual))
            {
                Need(method is "first" or "last" or "next" or "prev" && args.Count == 1,
                    "unsupported associative method or argument count");
                return $"{{\"kind\":\"assoc_traverse\",\"name\":{Q(SignalName(receiver!))},\"operation\":{Q(method)},\"index\":{Expr(args[0])}}}";
            }
            Need(QueueArray(actual), "method requires a queue");
            bool push = method is "push_front" or "push_back";
            bool pop = method is "pop_front" or "pop_back";
            Need((push && args.Count == 1) || (pop && args.Count == 0), "unsupported queue method or argument count");
            return $"{{\"kind\":{Q(push ? "queue_push" : "queue_pop")},\"name\":{Q(SignalName(receiver!))},\"front\":{(method is "push_front" or "pop_front" ? "true" : "false")}{(push ? ",\"value\":" + Expr(args[0]) : "")}}}";
        }

        if (h.VpiType == UhdmVpi.VpiTaskCall) return LowerTask(h, depth + 1);
        if (h.VpiType == UhdmVpi.VpiRepeat)
        {
            var count = h.One(UhdmVpi.VpiCondition);
            if (TypeOf(count) == UhdmVpi.VpiConstant)
            {
                ulong n = SelectedConstant(count);
                Need(n <= 256, "repeat count exceeds256");
                return $"{{\"kind\":\"repeat\",\"count\":{n},\"body\":{Stmt(h.One(UhdmVpi.VpiStmt), depth + 1)}}}";
            }
            return $"{{\"kind\":\"runtime_repeat\",\"count\":{Expr(count)},\"body\":{Stmt(h.One(UhdmVpi.VpiStmt), depth + 1)}}}";
        }
        if (h.VpiType is UhdmVpi.VpiWhile or UhdmVpi.VpiDoWhile)
            return $"{{\"kind\":{Q(h.VpiType == UhdmVpi.VpiWhile ? "while" : "do")},\"condition\":{Expr(h.One(UhdmVpi.VpiCondition))},\"body\":{Stmt(h.One(UhdmVpi.VpiStmt), depth + 1)}}}";
        if (h.VpiType == UhdmVpi.VpiFor)
        {
            Need(h.Many(UhdmVpi.VpiVariables).Count == 0, "runtime for declarations require module-scope counters");
            var init = new List<UhdmNode>();
            var inc = new List<UhdmNode>();
            static void AppendUnique(List<UhdmNode> list, UhdmNode? item)
            {
                if (item is not null && !list.Any(old => ReferenceEquals(old, item))) list.Add(item);
            }
            foreach (var item in h.GetFieldList("VpiForInitStmts")) AppendUnique(init, item);
            AppendUnique(init, h.One(UhdmVpi.VpiForInitStmt));
            foreach (var item in h.GetFieldList("VpiForIncStmts")) AppendUnique(inc, item);
            AppendUnique(inc, h.One(UhdmVpi.VpiForIncStmt));
            Need(init.Count <= 16 && inc.Count <= 16 && h.One(UhdmVpi.VpiCondition) is not null,
                "bounded for control list and condition required");
            var sb = new StringBuilder("{\"kind\":\"block\",\"items\":[");
            foreach (var item in init) sb.Append(Stmt(item, depth + 1, true)).Append(',');
            if (timedLowering)
            {
                sb.Append("{\"kind\":\"for\",\"condition\":").Append(Expr(h.One(UhdmVpi.VpiCondition)))
                  .Append(",\"step\":{\"kind\":\"block\",\"items\":[");
                for (int i = 0; i < inc.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Stmt(inc[i], depth + 1, true));
                }
                return sb.Append("]},\"body\":").Append(Stmt(h.One(UhdmVpi.VpiStmt), depth + 1)).Append("}]}").ToString();
            }
            sb.Append("{\"kind\":\"while\",\"condition\":").Append(Expr(h.One(UhdmVpi.VpiCondition)))
              .Append(",\"body\":{\"kind\":\"block\",\"items\":[").Append(Stmt(h.One(UhdmVpi.VpiStmt), depth + 1));
            foreach (var item in inc) sb.Append(',').Append(Stmt(item, depth + 1, true));
            return sb.Append("]}}]}").ToString();
        }
        if (h.VpiType == UhdmVpi.VpiOperation)
        {
            long op = h.GetInt(UhdmVpi.VpiOpType);
            Need(op is UhdmVpi.VpiPostIncOp or UhdmVpi.VpiPreIncOp or UhdmVpi.VpiPostDecOp or UhdmVpi.VpiPreDecOp,
                "only standalone increment/decrement statements supported");
            var args = h.Many(UhdmVpi.VpiOperand);
            Need(args.Count == 1, "increment requires one destination");
            string target = Expr(args[0]);
            return $"{{\"kind\":\"blocking\",\"target\":{target},\"value\":{{\"kind\":{Q(op is UhdmVpi.VpiPostDecOp or UhdmVpi.VpiPreDecOp ? "sub" : "add")},\"args\":[{target},{{\"kind\":\"const\",\"bits\":\"00000000000000000000000000000001\",\"signed\":true}}]}}}}";
        }
        if (h.VpiType == UhdmVpi.VpiBegin)
        {
            var sb = new StringBuilder("{\"kind\":\"block\",\"items\":[");
            bool first = true;
            foreach (var a in h.Many(UhdmVpi.VpiStmt))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Stmt(a, depth + 1));
            }
            return sb.Append("]}").ToString();
        }
        if (h.VpiType == UhdmVpi.VpiAssignment)
        {
            var assignDelay = h.One(UhdmVpi.VpiDelayControl);
            Need((assignDelay is null || timedLowering) && h.One(UhdmVpi.VpiEventControl) is null,
                "assignment timing unsupported");
            if (assignDelay is not null)
            {
                Need(!control, "timed loop control assignment unsupported");
                TargetShape(h.One(UhdmVpi.VpiLhs));
                return $"{{\"kind\":{Q(h.GetBool(UhdmVpi.VpiBlocking) ? "blocking_delay" : "nba")},{DelayFields(assignDelay)},\"target\":{Expr(h.One(UhdmVpi.VpiLhs))},\"value\":{Expr(h.One(UhdmVpi.VpiRhs))}}}";
            }
            var lhs = h.One(UhdmVpi.VpiLhs);
            var rhs = h.One(UhdmVpi.VpiRhs);
            var array = lhs?.One(UhdmVpi.VpiActual);
            if (TypeOf(rhs) == UhdmVpi.VpiHierPath)
            {
                var elements = rhs!.GetFieldList("Path_elems");
                if (elements.Count == 2)
                {
                    var receiver = elements[0];
                    var method = elements[1];
                    var actual = receiver.One(UhdmVpi.VpiActual);
                    string methodName = method.Name;
                    if (AssociativeArray(actual) && methodName is "first" or "last" or "next" or "prev")
                    {
                        var args = method.Many(UhdmVpi.VpiArgument);
                        Need(!insideCall && h.GetBool(UhdmVpi.VpiBlocking) && TypeOf(lhs) == UhdmVpi.VpiRefObj &&
                            flatSymbols.ContainsKey(actual!) && method.VpiType == UhdmVpi.VpiMethodFuncCall && args.Count == 1,
                            "associative traversal requires a blocking whole-word assignment and one index variable");
                        return $"{{\"kind\":\"assoc_traverse\",\"name\":{Q(SignalName(receiver))},\"operation\":{Q(methodName)},\"index\":{Expr(args[0])},\"target\":{Expr(lhs)}}}";
                    }
                    if (QueueArray(actual) && methodName is "pop_front" or "pop_back")
                    {
                        Need(!insideCall && h.GetBool(UhdmVpi.VpiBlocking) && TypeOf(lhs) == UhdmVpi.VpiRefObj &&
                            flatSymbols.ContainsKey(actual!) && method.VpiType == UhdmVpi.VpiMethodFuncCall && method.Many(UhdmVpi.VpiArgument).Count == 0,
                            "queue pop requires a blocking whole-word assignment and no arguments");
                        return $"{{\"kind\":\"queue_pop\",\"name\":{Q(SignalName(receiver))},\"front\":{(methodName == "pop_front" ? "true" : "false")},\"target\":{Expr(lhs)}}}";
                    }
                }
            }
            if (TypeOf(rhs) == UhdmVpi.VpiMethodFuncCall && rhs!.Name == "new")
            {
                Need(!insideCall && h.GetBool(UhdmVpi.VpiBlocking) && TypeOf(lhs) == UhdmVpi.VpiRefObj &&
                    DynamicArray(array) && flatSymbols.ContainsKey(array!),
                    "dynamic allocation requires a blocking whole-array destination");
                var args = rhs.Many(UhdmVpi.VpiArgument);
                Need(args.Count is 1 or 2, "dynamic new requires count and optional self initializer");
                if (args.Count == 2)
                    Need(args[1].VpiType == UhdmVpi.VpiRefObj && ReferenceEquals(args[1].One(UhdmVpi.VpiActual), array),
                        "dynamic resize initializer must be the same array declaration");
                return $"{{\"kind\":\"allocate\",\"name\":{Q(SignalName(lhs!))},\"count\":{Expr(args[0])},\"preserve\":{(args.Count == 2 ? "true" : "false")}}}";
            }
            TargetShape(lhs);
            string lhsJson = TypeOf(lhs) == UhdmVpi.VpiHierPath ? PackedFieldExpression(lhs!, true) : Expr(lhs);
            return $"{{\"kind\":{Q(control || h.GetBool(UhdmVpi.VpiBlocking) ? "blocking" : "nba")},\"target\":{lhsJson},\"value\":{Expr(rhs)}}}";
        }
        if (h.VpiType is UhdmVpi.VpiIf or UhdmVpi.VpiIfElse)
        {
            var e = h.One(UhdmVpi.VpiElseStmt);
            return $"{{\"kind\":\"if\",\"condition\":{Expr(h.One(UhdmVpi.VpiCondition))},\"then\":{Stmt(h.One(UhdmVpi.VpiStmt), depth + 1)},\"else\":{(e is not null ? Stmt(e, depth + 1) : "{\"kind\":\"block\",\"items\":[]}")}}}";
        }
        if (h.VpiType == UhdmVpi.VpiCase)
        {
            long ct = h.GetInt(UhdmVpi.VpiCaseType);
            Need(ct is UhdmVpi.VpiCaseExact or UhdmVpi.VpiCaseZ or UhdmVpi.VpiCaseX, "unsupported case type");
            string ck = ct == UhdmVpi.VpiCaseZ ? "casez" : ct == UhdmVpi.VpiCaseX ? "casex" : string.Empty;
            var sb = new StringBuilder($"{{\"kind\":\"case\"{(ck.Length == 0 ? "" : ",\"case_kind\":" + Q(ck))},\"value\":{Expr(h.One(UhdmVpi.VpiCondition))},\"items\":[");
            bool first = true;
            foreach (var item in h.Many(UhdmVpi.VpiCaseItem))
            {
                if (!first) sb.Append(',');
                first = false;
                var conditions = item.Many(UhdmVpi.VpiExpr);
                if (conditions.Count == 0) sb.Append("{\"match\":null");
                else if (conditions.Count == 1) sb.Append("{\"match\":").Append(Expr(conditions[0]));
                else
                {
                    Need(conditions.Count <= 256, "too many case labels in one item");
                    sb.Append("{\"matches\":[");
                    for (int ci = 0; ci < conditions.Count; ci++)
                    {
                        if (ci > 0) sb.Append(',');
                        sb.Append(Expr(conditions[ci]));
                    }
                    sb.Append(']');
                }
                sb.Append(",\"body\":").Append(Stmt(item.One(UhdmVpi.VpiStmt), depth + 1)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        throw new InvalidOperationException("unsupported statement type " + h.VpiType);
    }

    private string Stmt(UhdmNode? h, int depth = 0, bool control = false)
    {
        string result = StmtImpl(h, depth, control);
        if (!timedLowering) return result;
        string file = h!.File;
        if (file.Length == 0 || h.LineNo == 0) return result;
        Need(result.Length > 1 && result[0] == '{', "statement JSON object required");
        string source = $"\"file\":{Q(file)},\"line\":{h.LineNo}";
        if (h.ColumnNo > 0) source += $",\"column\":{h.ColumnNo}";
        return $"{{\"source\":{{{source}}},{result[1..]}";
    }
}
