using System.Text;

namespace VeloSim.Uhdm;

public sealed partial class UhdmRtlLowerer
{
    private sealed class LocalWord(UhdmNode declaration, string value = "", bool assigned = false)
    {
        public UhdmNode Declaration { get; } = declaration;
        public string Value { get; set; } = value;
        public bool Assigned { get; set; } = assigned;
        public LocalWord Clone() => new(Declaration, Value, Assigned);
    }

    private readonly record struct KnownControl(ulong Bits, int Width, bool Sign);

    private sealed class ParameterBinding(UhdmNode declaration, UhdmNode? rhs, UhdmNode scope, UhdmNode context, UhdmNode canonical)
    {
        public UhdmNode Declaration { get; } = declaration;
        public UhdmNode? Rhs { get; set; } = rhs;
        public UhdmNode Scope { get; } = scope;
        public UhdmNode Context { get; } = context;
        public UhdmNode Canonical { get; } = canonical;
    }

    private SortedDictionary<string, LocalWord> callLocals = new(StringComparer.Ordinal);
    private bool insideCall;
    private int loopVarCounter;
    private readonly HashSet<UhdmNode> activeCalls = new(ReferenceEqualityComparer.Instance);
    private UhdmNode? currentFunction;

    private readonly Dictionary<string, UhdmNode?> syntaxParameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UhdmNode?> syntaxAssignments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UhdmNode?> syntaxReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UhdmNode?> syntaxParameterValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UhdmNode?> syntaxVariableReferences = new(StringComparer.Ordinal);
    private readonly HashSet<UhdmNode> syntaxVisited = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> syntaxNonparameters = new(StringComparer.Ordinal);
    private readonly HashSet<string> syntaxParameterSelections = new(StringComparer.Ordinal);
    private int syntaxSymbolChecks;

    private UhdmNode? parameterContext;
    private readonly List<UhdmNode> parameterVisibleScopes = [];
    private readonly List<ParameterBinding> parameterBindings = [];

    private Dictionary<string, KnownControl> controlValues = new(StringComparer.Ordinal);
    private readonly HashSet<UhdmNode> loopControls = new(ReferenceEqualityComparer.Instance);

    private static SortedDictionary<string, LocalWord> CloneLocals(SortedDictionary<string, LocalWord> src)
    {
        var copy = new SortedDictionary<string, LocalWord>(StringComparer.Ordinal);
        foreach (var (k, v) in src) copy[k] = v.Clone();
        return copy;
    }

    private static UhdmNode Callee(UhdmNode call, bool function)
    {
        var h = call.One(function ? UhdmVpi.VpiFunction : UhdmVpi.VpiTask);
        Need(h is not null, "unresolved subroutine call");
        return h!;
    }

    private List<UhdmNode?> CallArguments(UhdmNode call)
    {
        bool qualified = false;
        foreach (var attr in mod!.Many(UhdmVpi.VpiAttribute))
            qualified |= attr.Name == "velosim frontend argument slots v4";
        Need(qualified, "subroutine calls require preserved argument slots; re-elaborate with the argument-slot frontend overlay");
        Need(call.VpiType is UhdmVpi.VpiFuncCall or UhdmVpi.VpiTaskCall, "typed subroutine call required");
        var args = call.GetFieldListWithNulls("Tf_call_args");
        var result = new List<UhdmNode?>(args.Count);
        Need(args.Count <= 32, "subroutine argument budget exceeded");
        foreach (var arg in args)
        {
            UhdmNode? h = arg;
            if (h is not null && h.VpiType == UhdmVpi.VpiConstant && h.GetInt(UhdmVpi.VpiConstType) == UhdmVpi.VpiIntConst)
            {
                string marker = h.GetString(UhdmVpi.VpiDecompile);
                Need(marker != "$velosim_invalid_actual_v4", "invalid named or positional subroutine arguments");
                if (marker == "$velosim_omitted_actual_v4") h = null;
            }
            result.Add(h);
        }
        return result;
    }

    private static UhdmNode FormalWord(UhdmNode h)
    {
        var binding = h.One(UhdmVpi.VpiExpr);
        int kind = TypeOf(binding);
        bool isDecl = kind is UhdmVpi.VpiReg or UhdmVpi.VpiLogicVar or UhdmVpi.VpiBitVar or UhdmVpi.VpiIntVar
            or UhdmVpi.VpiIntegerVar or UhdmVpi.VpiLongIntVar or UhdmVpi.VpiShortIntVar or UhdmVpi.VpiByteVar
            or UhdmVpi.VpiTimeVar or UhdmVpi.VpiEnumVar or UhdmVpi.VpiStructVar or UhdmVpi.VpiUnionVar
            or UhdmVpi.VpiPackedArrayVar;
        return binding is not null && isDecl ? binding : h;
    }

    private static UhdmNode? FormalDefault(UhdmNode h)
    {
        var binding = h.One(UhdmVpi.VpiExpr);
        return binding is not null && ReferenceEquals(binding, FormalWord(h)) ? binding.One(UhdmVpi.VpiExpr) : binding;
    }

    private string DefaultArgument(UhdmNode value)
    {
        var checkedNodes = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
        void Validate(UhdmNode? node, int depth)
        {
            Need(node is not null && depth < 64, "default argument expression depth exceeded");
            if (!checkedNodes.Add(node!)) return;
            Need(checkedNodes.Count <= 20_000, "default argument expression budget exceeded");
            if (node!.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar or UhdmVpi.VpiBitSelect
                or UhdmVpi.VpiPartSelect or UhdmVpi.VpiIndexedPartSelect or UhdmVpi.VpiVarSelect)
            {
                var declaration = node.One(UhdmVpi.VpiActual);
                var seen = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
                for (int i = 0; declaration is not null && i < 64; i++)
                {
                    Need(seen.Add(declaration), "cyclic default argument binding");
                    var owner = declaration.Parent;
                    for (int j = 0; owner is not null && j < 64; j++)
                    {
                        Need(owner.VpiType is not (UhdmVpi.VpiFunction or UhdmVpi.VpiTask),
                            "default argument cannot capture automatic subroutine storage");
                        if (owner.VpiType is UhdmVpi.VpiModule or UhdmVpi.VpiGenScope) break;
                        owner = owner.Parent;
                    }
                    var next = declaration.One(UhdmVpi.VpiActual);
                    if (next is null || ReferenceEquals(next, declaration)) break;
                    declaration = next;
                }
            }
            foreach (int rel in new[] { UhdmVpi.VpiOperand, UhdmVpi.VpiArgument, UhdmVpi.VpiIndex })
                foreach (var child in node.Many(rel))
                    Validate(child, depth + 1);
            var idx = node.One(UhdmVpi.VpiIndex);
            if (idx is not null) Validate(idx, depth + 1);
        }

        Validate(value, 0);
        bool prev = insideCall;
        insideCall = false;
        try { return Expr(value, 0); }
        finally { insideCall = prev; }
    }

    private static bool SameLocal(UhdmNode? a, UhdmNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.VpiType != b.VpiType) return false;
        string full = a.FullName;
        string other = b.FullName;
        return full.Length > 0 && full == other && a.File == b.File && a.LineNo != 0 && a.LineNo == b.LineNo && a.ColumnNo == b.ColumnNo;
    }

    private string LocalKey(UhdmNode? h)
    {
        for (int i = 0; h is not null && i < 64; i++)
        {
            foreach (var (key, word) in callLocals)
                if (SameLocal(h, word.Declaration)) return key;
            var actual = h.One(UhdmVpi.VpiActual);
            if (actual is null && h.VpiType == UhdmVpi.VpiIODecl) actual = h.One(UhdmVpi.VpiExpr);
            if (ReferenceEquals(actual, h)) break;
            h = actual;
        }
        return string.Empty;
    }

    private string ConvertWord(UhdmNode declaration, string value)
    {
        OutputBudget(value);
        for (int i = 0; i < 64; i++)
        {
            var actual = declaration.One(UhdmVpi.VpiActual);
            if (actual is null || ReferenceEquals(actual, declaration)) break;
            declaration = actual;
        }
        if (TypeOf(WordSpec(declaration)) != UhdmVpi.VpiIntTypespec) LogicWord(declaration);
        return $"{{\"kind\":\"convert\",\"value\":{value},\"width\":{Width(declaration)},\"signed\":{(DeclaredSigned(declaration) ? "true" : "false")},\"two_state\":{(TwoStateWord(declaration) ? "true" : "false")}}}";
    }

    private static UhdmNode ParameterOwner(UhdmNode declaration)
    {
        var seen = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
        var current = declaration.Parent;
        for (int depth = 0; current is not null && depth < 64; depth++)
        {
            Need(seen.Add(current), "cyclic parameter owner chain");
            int t = current.VpiType;
            if (t is UhdmVpi.VpiFunction or UhdmVpi.VpiTask or UhdmVpi.VpiModule or UhdmVpi.VpiGenScope)
                return current;
            current = current.Parent;
        }
        throw new InvalidOperationException("parameter declaration has no bounded lexical owner");
    }

    private static bool OwnedParameter(UhdmNode declaration, UhdmNode scope)
    {
        var owner = ParameterOwner(declaration);
        return ReferenceEquals(owner, scope) || SameLocal(owner, scope);
    }

    private static string LexicalKey(UhdmNode? h)
    {
        string key = ExpressionLocation(h);
        for (int i = 0; i < 2 && key.Length > 0; i++)
        {
            int idx = key.LastIndexOf(':');
            if (idx < 0) break;
            key = key[..idx];
        }
        return key;
    }

    private static string OwnerDefinition(UhdmNode? scope)
    {
        var seen = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
        for (int i = 0; scope is not null && i < 64; i++)
        {
            Need(seen.Add(scope), "cyclic definition scope");
            if (scope.VpiType == UhdmVpi.VpiModule) return scope.DefName;
            scope = scope.Parent;
        }
        return string.Empty;
    }

    private static string CanonicalKey(UhdmNode declaration)
    {
        var owner = ParameterOwner(declaration);
        return $"{LexicalKey(declaration)}|{owner.VpiType}|{(owner.VpiType == UhdmVpi.VpiModule ? OwnerDefinition(owner) : $"{LexicalKey(owner)}|{OwnerDefinition(owner)}")}";
    }

    private static void SyntaxBinding(Dictionary<string, UhdmNode?> entries, UhdmNode syntax, UhdmNode declaration)
    {
        string key = LexicalKey(syntax);
        if (key.Length == 0) return;
        if (!entries.TryGetValue(key, out var existing))
            entries[key] = declaration;
        else if (existing is null || CanonicalKey(existing) != CanonicalKey(declaration))
            entries[key] = null;
    }

    private UhdmNode? OriginalLexicalDeclaration(UhdmNode? reference)
    {
        if (reference is null) return null;
        string symbol = reference.Name;
        if (symbol.Length == 0) return null;
        var seen = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
        var scope = reference.Parent;
        for (int depth = 0; scope is not null && depth < 128; depth++)
        {
            Need(seen.Add(scope), "cyclic original lexical scope");
            UhdmNode? found = null;
            void Consider(UhdmNode? decl)
            {
                if (decl is null) return;
                Need(++syntaxSymbolChecks <= 1_000_000, "original symbol resolution budget exceeded");
                if (decl.Name != symbol) return;
                if (decl.VpiType == UhdmVpi.VpiParameter && !OwnedParameter(decl, scope)) return;
                if (found is not null && !ReferenceEquals(found, decl))
                    Need(SameLocal(found, decl), "ambiguous original lexical symbol");
                else
                    found = decl;
            }
            foreach (int rel in new[] { UhdmVpi.VpiParameter, UhdmVpi.VpiVariables, UhdmVpi.VpiReg, UhdmVpi.VpiNet, UhdmVpi.VpiIODecl })
                foreach (var decl in scope.Many(rel)) Consider(decl);
            foreach (var pa in scope.Many(UhdmVpi.VpiParamAssign))
            {
                var decl = pa.One(UhdmVpi.VpiLhs);
                if (TypeOf(decl) == UhdmVpi.VpiParameter && OwnedParameter(decl!, scope)) Consider(decl);
            }
            if (found is not null) return found;
            scope = scope.Parent;
        }
        return null;
    }

    private bool IsParameterReference(UhdmNode? h)
    {
        if (TypeOf(h) is not (UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar)) return false;
        string key = LexicalKey(h);
        if (syntaxReferences.TryGetValue(key, out var original))
        {
            Need(!syntaxNonparameters.Contains(key), "ambiguous original parameter/variable reference");
            Need(original is not null, "ambiguous original parameter reference");
            return true;
        }
        if (syntaxNonparameters.Contains(key))
        {
            Need(TypeOf(h!.One(UhdmVpi.VpiActual)) != UhdmVpi.VpiParameter,
                "elaborated parameter conflicts with original lexical variable");
            return false;
        }
        return TypeOf(h!.One(UhdmVpi.VpiActual)) == UhdmVpi.VpiParameter;
    }

    private static readonly int[] SyntaxSingleRelations =
    [
        UhdmVpi.VpiStmt, UhdmVpi.VpiCondition, UhdmVpi.VpiElseStmt, UhdmVpi.VpiLhs, UhdmVpi.VpiRhs,
        UhdmVpi.VpiReturn, UhdmVpi.VpiForInitStmt, UhdmVpi.VpiForIncStmt, UhdmVpi.VpiHighConn,
        UhdmVpi.VpiLowConn, UhdmVpi.VpiExpr
    ];

    private static readonly int[] SyntaxManyRelations =
    [
        UhdmVpi.VpiModule, UhdmVpi.VpiTaskFunc, UhdmVpi.VpiPort, UhdmVpi.VpiVariables, UhdmVpi.VpiReg,
        UhdmVpi.VpiNet, UhdmVpi.VpiParameter, UhdmVpi.VpiParamAssign, UhdmVpi.VpiProcess, UhdmVpi.VpiContAssign,
        UhdmVpi.VpiStmt, UhdmVpi.VpiOperand, UhdmVpi.VpiArgument, UhdmVpi.VpiExpr, UhdmVpi.VpiCaseItem,
        UhdmVpi.VpiGenScopeArray, UhdmVpi.VpiGenScope
    ];

    private void CollectParameterSyntax(UhdmNode? h, int depth = 0)
    {
        if (h is null || !syntaxVisited.Add(h)) return;
        Need(depth < 128 && syntaxVisited.Count <= 100_000, "parameter syntax budget exceeded");

        if (h.VpiType is UhdmVpi.VpiBitSelect or UhdmVpi.VpiPartSelect or UhdmVpi.VpiIndexedPartSelect)
        {
            var baseNode = h.One(UhdmVpi.VpiActual);
            var decl = TypeOf(baseNode) == UhdmVpi.VpiParameter ? baseNode : OriginalLexicalDeclaration(h.Name.Length == 0 ? baseNode : h);
            if (TypeOf(decl) == UhdmVpi.VpiParameter)
                syntaxParameterSelections.Add(LexicalKey(h));
        }

        if (h.VpiType == UhdmVpi.VpiHierPath)
        {
            foreach (var elem in h.GetFieldList("Path_elems"))
                CollectParameterSyntax(elem, depth + 1);
        }

        if (h.VpiType == UhdmVpi.VpiParameter)
            SyntaxBinding(syntaxParameters, h, h);

        if (h.VpiType == UhdmVpi.VpiParamAssign && TypeOf(h.One(UhdmVpi.VpiLhs)) == UhdmVpi.VpiParameter)
        {
            SyntaxBinding(syntaxAssignments, h, h.One(UhdmVpi.VpiLhs)!);
            string key = LexicalKey(h);
            var rhs = h.One(UhdmVpi.VpiRhs);
            if (!syntaxParameterValues.TryGetValue(key, out var existing))
                syntaxParameterValues[key] = rhs;
            else if (ExpressionLocation(existing) != ExpressionLocation(rhs))
                syntaxParameterValues[key] = null;
        }

        if (h.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar)
        {
            var decl = OriginalLexicalDeclaration(h);
            if (TypeOf(decl) == UhdmVpi.VpiParameter)
                SyntaxBinding(syntaxReferences, h, decl!);
            else if (decl is not null)
            {
                syntaxNonparameters.Add(LexicalKey(h));
                SyntaxBinding(syntaxVariableReferences, h, decl);
            }
        }

        foreach (int rel in SyntaxSingleRelations)
            CollectParameterSyntax(h.One(rel), depth + 1);
        foreach (int rel in SyntaxManyRelations)
            foreach (var child in h.Many(rel))
                CollectParameterSyntax(child, depth + 1);
    }

    private UhdmNode CanonicalParameter(UhdmNode declaration)
    {
        if (!syntaxParameters.TryGetValue(LexicalKey(declaration), out var found)) return declaration;
        Need(found is not null, "ambiguous original parameter declaration");
        return found!;
    }

    private static bool ParameterDefinition(UhdmNode owner, UhdmNode scope)
    {
        if (owner.VpiType != scope.VpiType) return false;
        if (scope.VpiType == UhdmVpi.VpiModule)
            return owner.DefName.Length > 0 && owner.DefName == scope.DefName;
        return LexicalKey(owner) == LexicalKey(scope) && OwnerDefinition(owner) == OwnerDefinition(scope);
    }

    private void BindParameter(UhdmNode declaration, UhdmNode? rhs, UhdmNode scope, UhdmNode canonical)
    {
        Need(LexicalKey(canonical).Length > 0, "parameter requires lexical declaration coordinates");
        var owner = ParameterOwner(canonical);
        if (!ParameterDefinition(owner, scope)) return;
        declaration = canonical;
        var context = scope.VpiType is UhdmVpi.VpiFunction or UhdmVpi.VpiTask ? parameterContext : scope;
        Need(context is not null, "parameter requires an elaborated instance context");

        string canonKey = CanonicalKey(canonical);
        var prior = parameterBindings.FirstOrDefault(p =>
            ReferenceEquals(p.Context, context) && ReferenceEquals(p.Scope, scope) && CanonicalKey(p.Canonical) == canonKey);
        if (prior is null)
        {
            parameterBindings.Add(new ParameterBinding(declaration, rhs, scope, context!, canonical));
            return;
        }
        if (rhs is null) return;
        if (prior.Rhs is null) { prior.Rhs = rhs; return; }
        if (ReferenceEquals(prior.Rhs, rhs)) return;
        Need(prior.Rhs.VpiType == UhdmVpi.VpiConstant && rhs.VpiType == UhdmVpi.VpiConstant, "conflicting parameter declaration bindings");
        int w = TypeOf(WordSpec(declaration)) == UhdmVpi.VpiIntTypespec ? 32 : Width(declaration);
        string NormalizeBits(UhdmNode val)
        {
            string bv = Bits(val);
            if (bv.Length > w) return bv[^w..];
            char pad = SignedExpression(val, 0) && bv[0] == '1' ? '1' : '0';
            return new string(pad, w - bv.Length) + bv;
        }
        Need(NormalizeBits(prior.Rhs) == NormalizeBits(rhs), "conflicting parameter declaration bindings");
    }

    private void BindParameters(UhdmNode scope)
    {
        var declarations = scope.Many(UhdmVpi.VpiParameter);
        foreach (var assignment in scope.Many(UhdmVpi.VpiParamAssign))
        {
            var decl = assignment.One(UhdmVpi.VpiLhs);
            Need(TypeOf(decl) == UhdmVpi.VpiParameter, "parameter binding requires declaration identity");
            var canonical = CanonicalParameter(decl!);
            if (syntaxAssignments.TryGetValue(LexicalKey(assignment), out var origAssign))
            {
                Need(origAssign is not null, "ambiguous original parameter assignment");
                canonical = origAssign!;
            }
            if (!ParameterDefinition(ParameterOwner(canonical), scope)) continue;

            var match = declarations.FirstOrDefault(item => LexicalKey(item) == LexicalKey(canonical));
            if (match is not null) decl = match;
            else if (LexicalKey(decl) != LexicalKey(canonical)) decl = canonical;

            var rhs = assignment.One(UhdmVpi.VpiRhs);
            if (!assignment.GetFieldBool("VpiOverriden") && syntaxParameterValues.TryGetValue(LexicalKey(assignment), out var srcVal))
            {
                Need(srcVal is not null, "ambiguous original parameter value");
                rhs = srcVal;
            }
            else if (TypeOf(rhs) == UhdmVpi.VpiConstant)
            {
                var origVal = OriginalExpression(rhs);
                if (TypeOf(origVal) == UhdmVpi.VpiOperation) rhs = origVal;
            }
            BindParameter(decl!, rhs, scope, canonical);
        }
        foreach (var decl in declarations)
            BindParameter(decl, null, scope, CanonicalParameter(decl));
    }

    private ParameterBinding FindParameterBinding(UhdmNode reference)
    {
        var param = reference.VpiType == UhdmVpi.VpiParameter ? reference : reference.One(UhdmVpi.VpiActual);
        UhdmNode canonical;
        if (syntaxReferences.TryGetValue(LexicalKey(reference), out var orig))
        {
            Need(orig is not null, "ambiguous original parameter reference");
            canonical = orig!;
        }
        else
        {
            Need(TypeOf(param) == UhdmVpi.VpiParameter, "parameter reference requires a declaration");
            canonical = CanonicalParameter(param!);
        }

        var owner = ParameterOwner(canonical);
        bool local = owner.VpiType is UhdmVpi.VpiFunction or UhdmVpi.VpiTask;
        if (local)
            Need(insideCall && currentFunction is not null && ParameterDefinition(owner, currentFunction),
                "parameter reference outside its lexical subroutine");

        string canonKey = CanonicalKey(canonical);
        ParameterBinding? found = null;
        foreach (var binding in parameterBindings)
        {
            if (CanonicalKey(binding.Canonical) != canonKey) continue;
            if (!parameterVisibleScopes.Any(s => ReferenceEquals(s, binding.Context))) continue;
            if (local && !ReferenceEquals(binding.Scope, currentFunction)) continue;
            Need(found is null, "ambiguous visible parameter binding");
            found = binding;
        }
        Need(found is not null, "parameter declaration is outside bound elaborated scopes");
        return found!;
    }

    private int ParameterReferenceWidth(UhdmNode reference)
    {
        var decl = FindParameterBinding(reference).Declaration;
        return TypeOf(WordSpec(decl)) == UhdmVpi.VpiIntTypespec ? 32 : Width(decl);
    }

    private bool ParameterReferenceSigned(UhdmNode reference) =>
        DeclaredSigned(FindParameterBinding(reference).Declaration);

    private string ParameterLiteral(UhdmNode value, int target)
    {
        if (value.VpiType == UhdmVpi.VpiConstant) return Bits(value);
        var operands = value.Many(UhdmVpi.VpiOperand);
        long op = value.GetInt(UhdmVpi.VpiOpType);
        Need(value.VpiType == UhdmVpi.VpiOperation && operands.Count == 1 &&
            (op == UhdmVpi.VpiMinusOp || op == UhdmVpi.VpiPlusOp) && operands[0].VpiType == UhdmVpi.VpiConstant,
            "parameter must be materialized or a unary literal");
        string b = Bits(operands[0]);
        Need(!b.Any(c => c is not ('0' or '1')), "captured parameter must have a known value");
        int w = Math.Max(target, b.Length);
        if (b.Length < w)
            b = new string(SignedExpression(operands[0], 0) && b[0] == '1' ? '1' : '0', w - b.Length) + b;
        if (op == UhdmVpi.VpiMinusOp)
        {
            char[] chars = b.ToCharArray();
            bool carry = true;
            for (int i = chars.Length - 1; i >= 0; i--)
            {
                bool bit = chars[i] == '0';
                chars[i] = (bit != carry) ? '1' : '0';
                carry = bit && carry;
            }
            b = new string(chars);
        }
        return b;
    }

    private string ParameterExpr(UhdmNode reference)
    {
        var found = FindParameterBinding(reference);
        var decl = found.Declaration;
        var spec = WordSpec(decl);
        Need(TypeOf(spec) is UhdmVpi.VpiIntTypespec or UhdmVpi.VpiLogicTypespec or UhdmVpi.VpiBitTypespec,
            "unsupported captured parameter type");
        int w = TypeOf(spec) == UhdmVpi.VpiIntTypespec ? 32 : Width(decl);
        string b;
        if (found.Rhs is not null)
        {
            b = ParameterLiteral(found.Rhs, w);
        }
        else
        {
            string pv = decl.GetFieldString("VpiValue");
            Need(pv.Length > 0, "unmaterialized parameter value");
            b = ConstantBits(w, pv);
        }
        Need(b.Length > 0 && !b.Any(c => c is not ('0' or '1')), "captured parameter must have a known value");
        if (b.Length > w) b = b[^w..];
        else if (b.Length < w)
            b = new string(found.Rhs is not null && SignedExpression(found.Rhs, 0) && b[0] == '1' ? '1' : '0', w - b.Length) + b;
        return $"{{\"kind\":\"const\",\"bits\":{Q(b)}{(DeclaredSigned(decl) ? ",\"signed\":true" : "")}}}";
    }

    private static ulong ControlMask(int w) => w >= 64 ? ulong.MaxValue : (1UL << w) - 1UL;

    private static ulong ControlResize(KnownControl a, int width, bool sign)
    {
        ulong value = a.Bits;
        if (sign && a.Sign && (value & (1UL << (a.Width - 1))) != 0)
            value |= ~ControlMask(a.Width);
        return value & ControlMask(width);
    }

    private bool ControlSigned(UhdmNode? h, int depth = 0)
    {
        Need(h is not null && depth < 64, "control signedness depth exceeded");
        if (h!.VpiType == UhdmVpi.VpiConstant) return SignedExpression(h, 0);
        if (IsParameterReference(h)) return ParameterReferenceSigned(h);
        string key = LocalKey(h);
        if (key.Length > 0) return DeclaredSigned(callLocals[key].Declaration);
        if (h.VpiType != UhdmVpi.VpiOperation) return false;
        long op = h.GetInt(UhdmVpi.VpiOpType);
        if (op is not (UhdmVpi.VpiAddOp or UhdmVpi.VpiSubOp or UhdmVpi.VpiMinusOp)) return false;
        var args = h.Many(UhdmVpi.VpiOperand);
        return args.Count > 0 && args.All(a => ControlSigned(a, depth + 1));
    }

    private KnownControl? ControlValue(UhdmNode? h, int depth = 0, int context = 0, bool? forceSign = null)
    {
        if (h is null || depth > 32 || context > 32) return null;
        var original = OriginalExpression(h);
        if (original is not null) return ControlValue(original, depth + 1, context, forceSign);
        if (h.VpiType == UhdmVpi.VpiConstant)
        {
            string b = Bits(h);
            if (b.Length > 32 || b.Any(c => c is not ('0' or '1'))) return null;
            return new KnownControl(Convert.ToUInt64(b, 2), b.Length, SignedExpression(h, 0));
        }
        if (IsParameterReference(h))
        {
            string text = ParameterExpr(h);
            int pos = text.IndexOf("\"bits\":\"", StringComparison.Ordinal);
            if (pos < 0) return null;
            int end = text.IndexOf('"', pos + 8);
            string b = text[(pos + 8)..end];
            if (b.Length > 32) return null;
            return new KnownControl(Convert.ToUInt64(b, 2), b.Length, ParameterReferenceSigned(h));
        }
        string key = LocalKey(h);
        if (key.Length > 0)
            return controlValues.TryGetValue(key, out var val) ? val : null;
        if (h.VpiType != UhdmVpi.VpiOperation) return null;
        var args = h.Many(UhdmVpi.VpiOperand);
        long op = h.GetInt(UhdmVpi.VpiOpType);
        if (args.Count == 1 && op == UhdmVpi.VpiMinusOp)
        {
            var a = ControlValue(args[0], depth + 1, context, forceSign);
            if (a is not { } av) return null;
            int w = Math.Max(av.Width, context);
            bool sign = forceSign ?? ControlSigned(h);
            ulong bits = unchecked((ulong)-(long)ControlResize(av, w, sign)) & ControlMask(w);
            return new KnownControl(bits, w, sign);
        }
        if (args.Count != 2) return null;
        bool comparison = op is UhdmVpi.VpiLtOp or UhdmVpi.VpiLeOp or UhdmVpi.VpiGtOp or UhdmVpi.VpiGeOp or UhdmVpi.VpiEqOp or UhdmVpi.VpiNeqOp;
        int operandWidth = Math.Max(Width(args[0]), Width(args[1]));
        if (!comparison) operandWidth = Math.Max(operandWidth, context);
        if (operandWidth > 32) return null;
        bool s = comparison ? (ControlSigned(args[0]) && ControlSigned(args[1])) : (forceSign ?? ControlSigned(h));
        var la = ControlValue(args[0], depth + 1, operandWidth, s);
        var lb = ControlValue(args[1], depth + 1, operandWidth, s);
        if (la is not { } av2 || lb is not { } bv2) return null;
        int rw = Math.Max(operandWidth, Math.Max(av2.Width, bv2.Width));
        ulong left = ControlResize(av2, rw, s), right = ControlResize(bv2, rw, s);
        long SignedVal(ulong v) => unchecked((long)(s && (v & (1UL << (rw - 1))) != 0 ? v | ~ControlMask(rw) : v));
        return op switch
        {
            UhdmVpi.VpiAddOp => new KnownControl((left + right) & ControlMask(rw), rw, s),
            UhdmVpi.VpiSubOp => new KnownControl((left - right) & ControlMask(rw), rw, s),
            UhdmVpi.VpiLtOp => new KnownControl(SignedVal(left) < SignedVal(right) ? 1UL : 0UL, 1, false),
            UhdmVpi.VpiLeOp => new KnownControl(SignedVal(left) <= SignedVal(right) ? 1UL : 0UL, 1, false),
            UhdmVpi.VpiGtOp => new KnownControl(SignedVal(left) > SignedVal(right) ? 1UL : 0UL, 1, false),
            UhdmVpi.VpiGeOp => new KnownControl(SignedVal(left) >= SignedVal(right) ? 1UL : 0UL, 1, false),
            UhdmVpi.VpiEqOp => new KnownControl(left == right ? 1UL : 0UL, 1, false),
            UhdmVpi.VpiNeqOp => new KnownControl(left != right ? 1UL : 0UL, 1, false),
            _ => null
        };
    }

    private static UhdmNode? ControlTarget(UhdmNode h)
    {
        if (h.VpiType == UhdmVpi.VpiAssignment) return h.One(UhdmVpi.VpiLhs);
        var args = h.Many(UhdmVpi.VpiOperand);
        Need(h.VpiType == UhdmVpi.VpiOperation && args.Count == 1, "unsupported for increment");
        long op = h.GetInt(UhdmVpi.VpiOpType);
        Need(op is UhdmVpi.VpiPostIncOp or UhdmVpi.VpiPreIncOp or UhdmVpi.VpiPostDecOp or UhdmVpi.VpiPreDecOp,
            "unsupported for increment");
        return args[0];
    }

    private static string BitsFromUlong(ulong value, int width)
    {
        var sb = new StringBuilder(width);
        for (int bit = width - 1; bit >= 0; bit--)
            sb.Append((value & (1UL << bit)) != 0 ? '1' : '0');
        return sb.ToString();
    }

    private void AssignLocal(UhdmNode h, int depth)
    {
        bool control = loopControls.Contains(h);
        if (h.VpiType == UhdmVpi.VpiOperation)
        {
            Need(control, "increment only supported as for control");
            string k = LocalKey(ControlTarget(h));
            Need(k.Length > 0 && controlValues.ContainsKey(k), "for increment requires known counter");
            var val = controlValues[k];
            long op = h.GetInt(UhdmVpi.VpiOpType);
            ulong nextBits = unchecked(val.Bits + (op is UhdmVpi.VpiPostDecOp or UhdmVpi.VpiPreDecOp ? ulong.MaxValue : 1UL)) & ControlMask(val.Width);
            val = val with { Bits = nextBits };
            controlValues[k] = val;
            var w = callLocals[k];
            string b = BitsFromUlong(val.Bits, val.Width);
            w.Value = ConvertWord(w.Declaration, $"{{\"kind\":\"const\",\"bits\":{Q(b)}{(val.Sign ? ",\"signed\":true" : "")}}}");
            w.Assigned = true;
            return;
        }
        Need((control || h.GetBool(UhdmVpi.VpiBlocking)) && h.One(UhdmVpi.VpiDelayControl) is null && h.One(UhdmVpi.VpiEventControl) is null,
            "subroutine requires untimed blocking writes");
        var target = h.One(UhdmVpi.VpiLhs);
        Need(TypeOf(target) is UhdmVpi.VpiRefObj or UhdmVpi.VpiReg or UhdmVpi.VpiLogicVar or UhdmVpi.VpiBitVar or UhdmVpi.VpiIntVar or UhdmVpi.VpiRefVar,
            "subroutine writes require whole local words");
        string key = LocalKey(target);
        Need(key.Length > 0, "subroutine write is not bound to a local declaration: " + NameOf(target));
        var constant = ControlValue(h.One(UhdmVpi.VpiRhs), 0, Width(callLocals[key].Declaration));
        string exprVal = Expr(h.One(UhdmVpi.VpiRhs), depth + 1);
        var word = callLocals[key];
        word.Value = ConvertWord(word.Declaration, exprVal);
        word.Assigned = true;
        controlValues.Remove(key);
        int width = Width(word.Declaration);
        if (constant is { } cv && width <= 32)
        {
            var known = new KnownControl(ControlResize(cv, width, true), width, DeclaredSigned(word.Declaration));
            controlValues[key] = known;
            string b = BitsFromUlong(known.Bits, width);
            word.Value = ConvertWord(word.Declaration, $"{{\"kind\":\"const\",\"bits\":{Q(b)}{(known.Sign ? ",\"signed\":true" : "")}}}");
        }
        OutputBudget(word.Value);
    }

    private void CounterDependencies(UhdmNode? h, string counter, int depth = 0)
    {
        Need(h is not null && depth < 64, "for control expression depth exceeded");
        if (h!.VpiType == UhdmVpi.VpiOperation)
        {
            foreach (var child in h.Many(UhdmVpi.VpiOperand))
                CounterDependencies(child, counter, depth + 1);
            return;
        }
        if (h.VpiType == UhdmVpi.VpiConstant || IsParameterReference(h)) return;
        Need(LocalKey(h) == counter, "for bounds/increment may only read the counter and constants");
    }

    private void SymbolicStmt(UhdmNode? h, int depth)
    {
        var seq = new List<UhdmNode?> { h };
        SymbolicSequence(seq, depth);
    }

    private void SymbolicSequence(List<UhdmNode?> sequence, int depth)
    {
        Need(depth < 32, "subroutine statement depth exceeded");
        while (sequence.Count > 0)
        {
            var h = sequence[0];
            sequence.RemoveAt(0);
            Need(h is not null && ++nodes <= 20_000, "subroutine statement budget exceeded");

            if (h!.VpiType == UhdmVpi.VpiBegin)
            {
                var items = h.Many(UhdmVpi.VpiStmt).Cast<UhdmNode?>().ToList();
                items.AddRange(sequence);
                sequence = items;
                continue;
            }
            if (h.VpiType == UhdmVpi.VpiAssignment || (h.VpiType == UhdmVpi.VpiOperation && loopControls.Contains(h)))
            {
                AssignLocal(h, depth);
                continue;
            }
            if (h.VpiType == UhdmVpi.VpiTaskCall)
            {
                SymbolicTaskCall(h, depth + 1);
                continue;
            }
            if (h.VpiType == UhdmVpi.VpiReturn)
            {
                var val = h.One(UhdmVpi.VpiCondition);
                if (currentFunction is not null && currentFunction.VpiType == UhdmVpi.VpiTask)
                {
                    Need(val is null, "task return cannot carry a value");
                    return;
                }
                Need(callLocals.ContainsKey("$return"), "function return declaration required");
                if (val is not null)
                {
                    string ex = Expr(val, depth + 1);
                    var w = callLocals["$return"];
                    w.Value = ConvertWord(w.Declaration, ex);
                    w.Assigned = true;
                }
                Need(callLocals["$return"].Assigned, "function return requires assigned result or expression");
                return;
            }
            if (h.VpiType == UhdmVpi.VpiRepeat)
            {
                var count = ControlValue(h.One(UhdmVpi.VpiCondition));
                Need(count is { } cv && cv.Bits <= 256 && !(cv.Sign && (cv.Bits & (1UL << (cv.Width - 1))) != 0),
                    "subroutine repeat requires known count0..256");
                var body = h.One(UhdmVpi.VpiStmt);
                var expanded = new List<UhdmNode?>((int)count!.Value.Bits + sequence.Count);
                for (ulong i = 0; i < count.Value.Bits; i++) expanded.Add(body);
                expanded.AddRange(sequence);
                sequence = expanded;
                continue;
            }
            if (h.VpiType == UhdmVpi.VpiFor)
            {
                foreach (var local in h.Many(UhdmVpi.VpiVariables))
                {
                    Need(local.One(UhdmVpi.VpiExpr) is null, "initialized loop declaration unsupported");
                    AddLocal(local);
                }
                var init = new List<UhdmNode>();
                var inc = new List<UhdmNode>();
                static void AppendUnique(List<UhdmNode> list, UhdmNode? item)
                {
                    if (item is not null && !list.Any(old => ReferenceEquals(old, item)))
                        list.Add(item);
                }
                foreach (var item in h.GetFieldList("VpiForInitStmts")) AppendUnique(init, item);
                AppendUnique(init, h.One(UhdmVpi.VpiForInitStmt));
                foreach (var item in h.GetFieldList("VpiForIncStmts")) AppendUnique(inc, item);
                AppendUnique(inc, h.One(UhdmVpi.VpiForIncStmt));

                Need(init.Count == 1 && inc.Count == 1 && init[0].VpiType == UhdmVpi.VpiAssignment &&
                    (inc[0].VpiType == UhdmVpi.VpiAssignment || inc[0].VpiType == UhdmVpi.VpiOperation),
                    "for requires one assignment initializer and increment");
                var initial = init[0].One(UhdmVpi.VpiLhs);
                if (LocalKey(initial).Length == 0 && TypeOf(initial) is UhdmVpi.VpiIntVar or UhdmVpi.VpiLogicVar or UhdmVpi.VpiBitVar)
                    AddLocal(initial!, "$loop" + (++loopVarCounter));
                string key = LocalKey(initial);
                Need(key.Length > 0 && key == LocalKey(ControlTarget(inc[0])) && Width(callLocals[key].Declaration) <= 32,
                    "for requires one <=32-bit local counter");

                // Prove loop body does not mutate its counter.
                var visit = new Stack<UhdmNode?>();
                visit.Push(h.One(UhdmVpi.VpiStmt));
                var bodySeen = new HashSet<UhdmNode>(ReferenceEqualityComparer.Instance);
                while (visit.Count > 0)
                {
                    var item = visit.Pop();
                    if (item is null || !bodySeen.Add(item)) continue;
                    Need(bodySeen.Count <= 20_000, "for body traversal budget exceeded");
                    if (item.VpiType == UhdmVpi.VpiAssignment)
                        Need(LocalKey(item.One(UhdmVpi.VpiLhs)) != key, "for body cannot mutate its counter");
                    if (item.VpiType == UhdmVpi.VpiTaskCall)
                    {
                        var arguments = CallArguments(item);
                        var ports = Callee(item, false).Many(UhdmVpi.VpiIODecl);
                        Need(arguments.Count <= ports.Count, "nested task arity mismatch");
                        for (int i = 0; i < ports.Count; i++)
                        {
                            if (ports[i].GetInt(UhdmVpi.VpiDirection) != UhdmVpi.VpiInput)
                            {
                                Need(i < arguments.Count && arguments[i] is not null, "missing nested task output argument");
                                Need(LocalKey(arguments[i]) != key, "nested task cannot mutate a proven loop counter");
                            }
                        }
                    }
                    if (item.VpiType == UhdmVpi.VpiOperation)
                    {
                        long op = item.GetInt(UhdmVpi.VpiOpType);
                        if (op is UhdmVpi.VpiPostIncOp or UhdmVpi.VpiPreIncOp or UhdmVpi.VpiPostDecOp or UhdmVpi.VpiPreDecOp)
                            Need(LocalKey(ControlTarget(item)) != key, "for body cannot mutate its counter");
                    }
                    if (item.VpiType == UhdmVpi.VpiFor)
                    {
                        foreach (var part in item.GetFieldList("VpiForInitStmts")) visit.Push(part);
                        visit.Push(item.One(UhdmVpi.VpiForInitStmt));
                        foreach (var part in item.GetFieldList("VpiForIncStmts")) visit.Push(part);
                        visit.Push(item.One(UhdmVpi.VpiForIncStmt));
                    }
                    foreach (var child in item.Many(UhdmVpi.VpiStmt)) visit.Push(child);
                    visit.Push(item.One(UhdmVpi.VpiStmt));
                    visit.Push(item.One(UhdmVpi.VpiElseStmt));
                }

                CounterDependencies(h.One(UhdmVpi.VpiCondition), key);
                CounterDependencies(inc[0].VpiType == UhdmVpi.VpiAssignment ? inc[0].One(UhdmVpi.VpiRhs) : ControlTarget(inc[0]), key);
                loopControls.Add(init[0]);
                loopControls.Add(inc[0]);

                var saved = CloneLocals(callLocals);
                var savedControls = new Dictionary<string, KnownControl>(controlValues, StringComparer.Ordinal);
                AssignLocal(init[0], depth + 1);
                var expanded = new List<UhdmNode?> { init[0] };
                var seenVals = new HashSet<ulong>();
                for (int i = 0; ; i++)
                {
                    var cond = ControlValue(h.One(UhdmVpi.VpiCondition));
                    Need(cond.HasValue, "for condition must be known and independent of runtime inputs");
                    if (cond!.Value.Bits == 0) break;
                    Need(i < 256 && controlValues.ContainsKey(key) && seenVals.Add(controlValues[key].Bits),
                        "for iteration budget or counter progress exceeded");
                    expanded.Add(h.One(UhdmVpi.VpiStmt));
                    expanded.Add(inc[0]);
                    AssignLocal(inc[0], depth + 1);
                }
                callLocals = saved;
                controlValues = savedControls;
                expanded.AddRange(sequence);
                sequence = expanded;
                continue;
            }
            if (h.VpiType is UhdmVpi.VpiIf or UhdmVpi.VpiIfElse)
            {
                string condExpr = Expr(h.One(UhdmVpi.VpiCondition), depth + 1);
                var before = CloneLocals(callLocals);
                var savedControls = new Dictionary<string, KnownControl>(controlValues, StringComparer.Ordinal);

                var yesSeq = new List<UhdmNode?>(sequence.Count + 1) { h.One(UhdmVpi.VpiStmt) };
                yesSeq.AddRange(sequence);
                SymbolicSequence(yesSeq, depth + 1);
                var yes = CloneLocals(callLocals);

                callLocals = CloneLocals(before);
                controlValues = new Dictionary<string, KnownControl>(savedControls, StringComparer.Ordinal);
                var noSeq = new List<UhdmNode?>(sequence.Count + 1);
                var other = h.One(UhdmVpi.VpiElseStmt);
                if (other is not null) noSeq.Add(other);
                noSeq.AddRange(sequence);
                SymbolicSequence(noSeq, depth + 1);
                var no = CloneLocals(callLocals);

                string truth = $"{{\"kind\":\"caseeq\",\"args\":[{{\"kind\":\"redor\",\"args\":[{condExpr}]}},{{\"kind\":\"const\",\"bits\":\"1\"}}]}}";
                controlValues.Clear();
                callLocals = before;
                int truthBytes = Encoding.UTF8.GetByteCount(truth);
                foreach (var (key, word) in callLocals)
                {
                    Need(Encoding.UTF8.GetByteCount(yes[key].Value) + Encoding.UTF8.GetByteCount(no[key].Value) + truthBytes < 1024 * 1024,
                        "symbolic branch expansion exceeds1MiB");
                    word.Assigned = yes[key].Assigned && no[key].Assigned;
                    if (word.Assigned)
                    {
                        word.Value = $"{{\"kind\":\"cond\",\"args\":[{truth},{yes[key].Value},{no[key].Value}]}}";
                        OutputBudget(word.Value);
                    }
                }
                return;
            }

            throw new InvalidOperationException("unsupported pure subroutine statement type " + h.VpiType);
        }
    }

    private CallFrameScope EnterCallFrame(UhdmNode function)
    {
        Need(activeCalls.Count < 16 && activeCalls.Add(function), "recursive or excessive subroutine calls");
        var scope = new CallFrameScope(this, callLocals, controlValues, insideCall, function, currentFunction);
        callLocals = new SortedDictionary<string, LocalWord>(StringComparer.Ordinal);
        controlValues = new Dictionary<string, KnownControl>(StringComparer.Ordinal);
        insideCall = true;
        currentFunction = function;
        return scope;
    }

    private readonly struct CallFrameScope(
        UhdmRtlLowerer owner,
        SortedDictionary<string, LocalWord> savedLocals,
        Dictionary<string, KnownControl> savedControls,
        bool wasInside,
        UhdmNode fn,
        UhdmNode? prevFunction) : IDisposable
    {
        public void Dispose()
        {
            owner.callLocals = savedLocals;
            owner.controlValues = savedControls;
            owner.insideCall = wasInside;
            owner.currentFunction = prevFunction;
            owner.activeCalls.Remove(fn);
        }
    }

    private void AddLocal(UhdmNode h, string key = "", bool initialized = false)
    {
        if (key.Length == 0) key = h.Name;
        Need(key.Length > 0 && (initialized || h.One(UhdmVpi.VpiExpr) is null),
            "initialized or unnamed subroutine local unsupported");
        if (TypeOf(WordSpec(h)) != UhdmVpi.VpiIntTypespec) LogicWord(h);
        Need(callLocals.Count < 64, "subroutine local budget exceeded");
        if (callLocals.TryGetValue(key, out var existing))
            Need(ReferenceEquals(existing.Declaration, h), "duplicate subroutine local name");
        else
            callLocals[key] = new LocalWord(h);
    }

    private IReadOnlyList<UhdmNode> PrepareCall(UhdmNode function, List<string> values, bool task)
    {
        Need(function.GetBool(UhdmVpi.VpiAutomatic), "only automatic subroutines supported");
        BindParameters(function);
        var formals = function.Many(UhdmVpi.VpiIODecl);
        Need(values.Count <= formals.Count && formals.Count <= 32, "subroutine arity mismatch or budget exceeded");

        var initializers = new List<UhdmNode>();
        foreach (int rel in new[] { UhdmVpi.VpiReg, UhdmVpi.VpiVariables })
        {
            foreach (var local in function.Many(rel))
            {
                AddLocal(local, string.Empty, true);
                if (local.One(UhdmVpi.VpiExpr) is not null && !formals.Any(f => SameLocal(local, FormalWord(f))))
                    initializers.Add(local);
            }
        }

        for (int i = 0; i < formals.Count; i++)
        {
            long dir = formals[i].GetInt(UhdmVpi.VpiDirection);
            Need(dir == UhdmVpi.VpiInput || (task && (dir == UhdmVpi.VpiOutput || dir == UhdmVpi.VpiInout)),
                "subroutine supports value inputs and task value-result formals only");
            var decl = FormalWord(formals[i]);
            string key = decl.Name;
            if (!callLocals.ContainsKey(key)) AddLocal(decl, string.Empty, true);
            if (dir == UhdmVpi.VpiInput || dir == UhdmVpi.VpiInout)
            {
                bool supplied = i < values.Count && values[i].Length > 0;
                var fallback = FormalDefault(formals[i]);
                Need(supplied || (dir == UhdmVpi.VpiInput && fallback is not null),
                    "missing subroutine input requires an explicit default");
                string converted = ConvertWord(decl, supplied ? values[i] : DefaultArgument(fallback!));
                var word = callLocals[key];
                word.Value = converted;
                word.Assigned = true;
            }
            else
            {
                Need(i < values.Count, "missing task output argument");
            }
        }

        initializers = initializers.OrderBy(a => a.LineNo).ThenBy(a => a.ColumnNo).ToList();
        foreach (var local in initializers)
        {
            string converted = ConvertWord(local, Expr(local.One(UhdmVpi.VpiExpr), 0));
            var word = callLocals[local.Name];
            word.Value = converted;
            word.Assigned = true;
        }
        return formals;
    }

    private string LowerFunction(UhdmNode call, int depth)
    {
        var function = Callee(call, true);
        var values = new List<string>();
        foreach (var actual in CallArguments(call))
            values.Add(actual is not null ? Expr(actual, depth + 1) : string.Empty);

        using var frame = EnterCallFrame(function);
        PrepareCall(function, values, false);
        var result = function.One(UhdmVpi.VpiReturn);
        Need(result is not null, "function return declaration required");
        AddLocal(result!, "$return");
        SymbolicStmt(function.One(UhdmVpi.VpiStmt), depth + 1);
        var word = callLocals["$return"];
        Need(word.Assigned, "function return must be assigned on every path");
        OutputBudget(word.Value);
        return word.Value;
    }

    private string LowerTask(UhdmNode call, int depth)
    {
        Need(!insideCall, "nested task calls unsupported");
        var function = Callee(call, false);
        var actuals = CallArguments(call);
        var formals = function.Many(UhdmVpi.VpiIODecl);
        Need(actuals.Count <= formals.Count, "task arity mismatch");

        var values = new List<string>(actuals.Count);
        var targets = new List<string>(actuals.Count);
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < actuals.Count; i++)
        {
            long dir = formals[i].GetInt(UhdmVpi.VpiDirection);
            if (actuals[i] is null)
            {
                Need(dir == UhdmVpi.VpiInput && FormalDefault(formals[i]) is not null,
                    "missing task input requires a default");
                values.Add(string.Empty);
                targets.Add(string.Empty);
                continue;
            }
            if (dir == UhdmVpi.VpiOutput || dir == UhdmVpi.VpiInout)
            {
                Need(actuals[i]!.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiReg or UhdmVpi.VpiLogicVar,
                    "task outputs require whole words");
                string target = SignalName(actuals[i]!);
                Need(outputs.Add(target), "aliased task outputs unsupported");
                string targetExpr = Expr(actuals[i], depth + 1);
                targets.Add(targetExpr);
                values.Add(dir == UhdmVpi.VpiInout ? Expr(actuals[i], depth + 1) : string.Empty);
            }
            else
            {
                values.Add(Expr(actuals[i], depth + 1));
                targets.Add(string.Empty);
            }
        }

        using var frame = EnterCallFrame(function);
        PrepareCall(function, values, true);
        SymbolicStmt(function.One(UhdmVpi.VpiStmt), depth + 1);

        var destinations = new List<string>();
        var results = new List<string>();
        int total = 0;
        for (int i = 0; i < formals.Count; i++)
        {
            long dir = formals[i].GetInt(UhdmVpi.VpiDirection);
            if (dir == UhdmVpi.VpiOutput || dir == UhdmVpi.VpiInout)
            {
                var word = callLocals[FormalWord(formals[i]).Name];
                Need(word.Assigned, "task outputs require assignment on every path");
                destinations.Add(targets[i]);
                results.Add(ConvertWord(actuals[i]!, word.Value));
                total += Width(actuals[i]);
                Need(total <= 256, "task combined outputs exceed256bits");
            }
        }
        if (destinations.Count == 0) return "{\"kind\":\"block\",\"items\":[]}";
        return $"{{\"kind\":\"blocking\",\"target\":{ConcatTree(destinations, 0, destinations.Count)},\"value\":{ConcatTree(results, 0, results.Count)}}}";
    }

    private void SymbolicTaskCall(UhdmNode call, int depth)
    {
        Need(currentFunction is not null && currentFunction.VpiType == UhdmVpi.VpiTask,
            "nested task calls currently require a task caller");
        var function = Callee(call, false);
        var arguments = CallArguments(call);
        var ports = function.Many(UhdmVpi.VpiIODecl);
        Need(arguments.Count <= ports.Count, "nested task arity mismatch");

        var values = new List<string>(ports.Count);
        var keys = new List<string>(ports.Count);
        var destinations = new List<UhdmNode?>(ports.Count);
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ports.Count; i++)
        {
            long dir = ports[i].GetInt(UhdmVpi.VpiDirection);
            if (i >= arguments.Count || arguments[i] is null)
            {
                Need(dir == UhdmVpi.VpiInput && FormalDefault(ports[i]) is not null,
                    "missing nested task input requires a default");
                values.Add(string.Empty);
                keys.Add(string.Empty);
                destinations.Add(null);
                continue;
            }
            if (dir == UhdmVpi.VpiOutput || dir == UhdmVpi.VpiInout)
            {
                Need(arguments[i]!.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar or UhdmVpi.VpiReg or UhdmVpi.VpiLogicVar,
                    "nested task outputs require whole local words");
                string k = LocalKey(arguments[i]);
                Need(k.Length > 0 && outputs.Add(k), "nested task requires distinct whole local outputs");
                keys.Add(k);
                destinations.Add(callLocals[k].Declaration);
            }
            else
            {
                keys.Add(string.Empty);
                destinations.Add(null);
            }
            values.Add(dir == UhdmVpi.VpiOutput ? string.Empty : Expr(arguments[i], depth + 1));
        }

        var results = new string[ports.Count];
        using (EnterCallFrame(function))
        {
            var formals = PrepareCall(function, values, true);
            SymbolicStmt(function.One(UhdmVpi.VpiStmt), depth + 1);
            for (int i = 0; i < formals.Count; i++)
            {
                if (keys[i].Length > 0)
                {
                    var word = callLocals[FormalWord(formals[i]).Name];
                    Need(word.Assigned, "nested task output must be assigned on every path");
                    results[i] = ConvertWord(destinations[i]!, word.Value);
                    OutputBudget(results[i]);
                }
            }
        }

        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].Length > 0)
            {
                var word = callLocals[keys[i]];
                word.Value = results[i];
                word.Assigned = true;
                controlValues.Remove(keys[i]);
            }
        }
    }
}
