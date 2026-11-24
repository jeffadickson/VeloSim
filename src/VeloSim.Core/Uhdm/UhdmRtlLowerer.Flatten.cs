using System.Text;

namespace VeloSim.Uhdm;

public sealed partial class UhdmRtlLowerer
{
    private sealed record FlatSignal(int Width, int Direction, string Path, bool IsSigned, bool TwoState);
    private sealed record FlatMemory(int Width, int Depth, bool IsSigned, int Lower, List<(int Left, int Right)> Dimensions,
        bool TwoState, bool Dynamic = false, bool Queue = false, bool Associative = false, int KeyWidth = 0, bool KeySigned = false);
    private sealed class FlatScope(UhdmNode handle, UhdmNode module, int parent, string path)
    {
        public UhdmNode Handle { get; } = handle;
        public UhdmNode Module { get; } = module;
        public int Parent { get; } = parent;
        public string Path { get; } = path;
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
    }
    private sealed record FlatBinding(int Parent, int Child, int Direction, UhdmNode High, UhdmNode Low, string Target);
    private sealed record FlatInitializer(int Scope, UhdmNode Declaration, UhdmNode Value);
    private sealed class FlatProcess(int scope, UhdmNode? body)
    {
        public int Scope { get; } = scope;
        public UhdmNode? Body { get; } = body;
        public List<(string Signal, bool Positive)> Events { get; } = [];
    }

    private readonly List<FlatScope> flatScopes = [];
    private readonly List<FlatInitializer> flatInitializers = [];
    private readonly HashSet<UhdmNode> initializedDeclarations = new(ReferenceEqualityComparer.Instance);
    private readonly SortedDictionary<string, FlatSignal> flatSignals = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, FlatMemory> flatMemories = new(StringComparer.Ordinal);
    private readonly HashSet<UhdmNode> visitedScopes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> usedFlatNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UhdmNode> originalPortInitializers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> expectedPortInitializers = new(StringComparer.Ordinal);

    private string InitializerKey(UhdmNode port, UhdmNode scope) =>
        $"{LexicalKey(port)}|{OwnerDefinition(scope)}|{port.Name}";

    private void CollectPortInitializers(UhdmNode scope)
    {
        foreach (var port in scope.Many(UhdmVpi.VpiPort))
        {
            var value = port.One(UhdmVpi.VpiHighConn);
            var declaration = port.One(UhdmVpi.VpiLowConn)?.One(UhdmVpi.VpiActual);
            if (port.GetInt(UhdmVpi.VpiDirection) != UhdmVpi.VpiOutput || value is null || declaration is null ||
                (declaration.VpiType == UhdmVpi.VpiNet && declaration.GetInt(UhdmVpi.VpiNetType) == UhdmVpi.VpiWire))
                continue;
            Need(LexicalKey(port).Length > 0, "output initializer requires source coordinates");
            string key = InitializerKey(port, scope);
            string ownerDef = OwnerDefinition(scope);
            if (!expectedPortInitializers.TryGetValue(ownerDef, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                expectedPortInitializers[ownerDef] = set;
            }
            set.Add(key);
            if (originalPortInitializers.TryGetValue(key, out var old))
                Need(ReferenceEquals(old, value), "ambiguous original output initializer");
            else
                originalPortInitializers[key] = value;
        }
    }

    private static string ScopeName(UhdmNode h)
    {
        string local = h.Name;
        if (local.Length > 0) return local;
        string full = h.FullName;
        int dot = full.LastIndexOf('.');
        Need(dot >= 0 && dot + 1 < full.Length, "unnamed hierarchy scope");
        return full[(dot + 1)..];
    }

    private void InScope(int id)
    {
        mod = flatScopes[id].Module;
        activeSymbols = flatScopes[id].Names;
        parameterContext = flatScopes[id].Handle;
        parameterVisibleScopes.Clear();
        for (int p = id; p >= 0; p = flatScopes[p].Parent)
            parameterVisibleScopes.Add(flatScopes[p].Handle);
    }

    private int CollectScope(UhdmNode scope, UhdmNode module, int parent, string path, int depth = 0)
    {
        Need(depth < 32 && flatScopes.Count < limits.MaxScopes, "hierarchy depth/scope budget exceeded");
        Need(visitedScopes.Add(scope), "duplicate elaborated scope");

        if (scope.Many(UhdmVpi.VpiTaskFunc).Count > 0)
        {
            bool qualified = false;
            foreach (var attr in module.Many(UhdmVpi.VpiAttribute))
                qualified |= attr.Name == "velosim frontend argument slots v4";
            Need(qualified, "subroutine declarations require constant-safe argument provenance; re-elaborate with the argument-slot frontend overlay");
        }

        int id = flatScopes.Count;
        var flatScope = new FlatScope(scope, module, parent, path);
        flatScopes.Add(flatScope);
        mod = module;
        BindParameters(scope);

        foreach (int forbidden in new[] { UhdmVpi.VpiPrimitive, UhdmVpi.VpiPrimitiveArray, UhdmVpi.VpiModuleArray })
            Need(scope.Many(forbidden).Count == 0, "primitives and module arrays unsupported");

        var directions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (parent < 0)
        {
            foreach (var port in scope.Many(UhdmVpi.VpiPort))
            {
                int direction = (int)port.GetInt(UhdmVpi.VpiDirection);
                Need(direction == UhdmVpi.VpiInput || direction == UhdmVpi.VpiOutput, "inout unsupported");
                directions[port.Name] = direction;
            }
        }

        foreach (int relation in new[] { UhdmVpi.VpiNet, UhdmVpi.VpiReg, UhdmVpi.VpiVariables, UhdmVpi.VpiNamedEvent })
        {
            foreach (var h in scope.Many(relation))
            {
                string local = h.Name;
                Need(local.Length > 0, "unnamed declaration");
                var initVal = h.One(UhdmVpi.VpiExpr);
                if (initVal is not null)
                {
                    Need(relation != UhdmVpi.VpiNet, "net declaration expressions require continuous assignment lowering");
                    if (initializedDeclarations.Add(h))
                        flatInitializers.Add(new FlatInitializer(id, h, initVal));
                }

                if (!flatScope.Names.TryGetValue(local, out string? flat))
                {
                    flat = path.Length == 0 ? local : $"vs_h{id}_{local}";
                    string baseName = flat;
                    int suffix = 0;
                    while (usedFlatNames.Contains(flat))
                        flat = $"{baseName}_{suffix++}";
                    usedFlatNames.Add(flat);
                    flatScope.Names[local] = flat;
                }

                flatSymbols[h] = flat;
                flatDeclarations[flat] = h;

                if (h.VpiType == UhdmVpi.VpiNamedEvent)
                {
                    flatNamedEvents.Add(flat);
                    Need(flatNamedEvents.Count <= 256, "named event budget exceeded");
                    continue;
                }

                if (h.VpiType == UhdmVpi.VpiArrayVar)
                {
                    if (QueueArray(h) || AssociativeArray(h))
                        Need(!directions.ContainsKey(local), "container ports unsupported; use module-scope storage");
                    Need(h.GetInt(UhdmVpi.VpiArrayType) == UhdmVpi.VpiStaticArray || LifetimeArray(h),
                        "associative arrays require separate container-lifetime lowering");
                    var ranges = h.Many(UhdmVpi.VpiRange);
                    var elements = h.Many(UhdmVpi.VpiReg);
                    if (elements.Count == 0) elements = h.Many(UhdmVpi.VpiVariables);

                    if (LifetimeArray(h))
                    {
                        if (QueueArray(h) && elements.Count == 1)
                            Need(!AggregateSpec(WordSpec(elements[0])), "queue aggregate elements unsupported");
                        Need(ranges.Count == 0 && elements.Count == 1 && h.One(UhdmVpi.VpiExpr) is null,
                            "dynamic arrays require one unpacked dimension and procedural allocation");
                        Need(elements[0].One(UhdmVpi.VpiExpr) is null, "dynamic element declaration initializers unsupported");
                        LogicWord(elements[0]);

                        int keyWidth = 0;
                        bool keySigned = false;
                        if (AssociativeArray(h))
                        {
                            var arraySpec = WordSpec(h);
                            Need(TypeOf(arraySpec) == UhdmVpi.VpiArrayTypespec, "associative array typespec required");
                            var key = BaseSpec(arraySpec!.One(UhdmVpi.VpiIndexTypespec)?.One(UhdmVpi.VpiActual));
                            keyWidth = AtomWidth(key);
                            keySigned = key is not null && key.GetBool(UhdmVpi.VpiSigned);
                            if (keyWidth == 0)
                            {
                                Need(TypeOf(key) is UhdmVpi.VpiLogicTypespec or UhdmVpi.VpiBitTypespec,
                                    "associative keys require an explicit integral type");
                                var keyRanges = key!.Many(UhdmVpi.VpiRange);
                                Need(keyRanges.Count <= 1, "multidimensional packed keys unsupported");
                                keyWidth = 1;
                                if (keyRanges.Count > 0)
                                {
                                    ulong left = Integer(keyRanges[0].One(UhdmVpi.VpiLeftRange));
                                    ulong right = Integer(keyRanges[0].One(UhdmVpi.VpiRightRange));
                                    Need(right == 0 && left < 256, "associative key range must be descending [width-1:0] through256bits");
                                    keyWidth = (int)(left + 1);
                                }
                            }
                        }

                        flatMemories[flat] = new FlatMemory(Width(elements[0]), 1, DeclaredSigned(elements[0]), 0, [],
                            TwoStateWord(elements[0]), DynamicArray(h), QueueArray(h), AssociativeArray(h), keyWidth, keySigned);
                        continue;
                    }

                    Need(ranges.Count is >= 1 and <= 4 && elements.Count == 1,
                        "fixed arrays require 1..4 dimensions and one element declaration");
                    Need(elements[0].One(UhdmVpi.VpiExpr) is null, "initialized memory element declarations unsupported");
                    LogicWord(elements[0]);
                    ulong product = 1;
                    var dimensions = new List<(int Left, int Right)>(ranges.Count);
                    foreach (var range in ranges)
                    {
                        ulong left = Integer(range.One(UhdmVpi.VpiLeftRange));
                        ulong right = Integer(range.One(UhdmVpi.VpiRightRange));
                        ulong lo = Math.Min(left, right), hi = Math.Max(left, right);
                        Need(hi <= int.MaxValue && hi - lo < limits.MaxMemDepth, "memory dimension exceeds depth limit");
                        product *= hi - lo + 1;
                        Need(product <= limits.MaxMemDepth, "memory dimension product exceeds depth limit");
                        dimensions.Add(((int)left, (int)right));
                    }
                    int lower = ranges.Count == 1 ? Math.Min(dimensions[0].Left, dimensions[0].Right) : 0;
                    if (ranges.Count == 1) dimensions.Clear();
                    flatMemories[flat] = new FlatMemory(Width(elements[0]), (int)product, DeclaredSigned(elements[0]), lower, dimensions, TwoStateWord(elements[0]));
                }
                else
                {
                    LogicWord(h);
                    if (relation == UhdmVpi.VpiNet && h.VpiType == UhdmVpi.VpiNet)
                    {
                        long netType = h.GetInt(UhdmVpi.VpiNetType);
                        Need(netType == UhdmVpi.VpiWire || netType == UhdmVpi.VpiLogicNet ||
                            (netType == 0 && (AtomWidth(WordSpec(h)) != 0 || AggregateSpec(WordSpec(h)))),
                            "resolved/pull net types unsupported");
                    }
                    int w = Width(h);
                    int dir = parent < 0 ? directions.GetValueOrDefault(local) : 0;
                    string fullPath = path.Length == 0 ? string.Empty : $"{path}.{local}";
                    if (flatSignals.TryGetValue(flat, out var old))
                        Need(old.Width == w, "conflicting signal widths");
                    else
                        flatSignals[flat] = new FlatSignal(w, dir, fullPath, DeclaredSigned(h), TwoStateWord(h));
                }
            }
        }

        Need(flatSignals.Count <= limits.MaxSignals && flatMemories.Count <= limits.MaxMemories,
            "flattened declaration budget exceeded");

        foreach (var child in scope.Many(UhdmVpi.VpiModule))
            CollectScope(child, child, id, path.Length == 0 ? ScopeName(child) : $"{path}.{ScopeName(child)}", depth + 1);
        foreach (var array in scope.Many(UhdmVpi.VpiGenScopeArray))
            foreach (var child in array.Many(UhdmVpi.VpiGenScope))
                if (!visitedScopes.Contains(child))
                    CollectScope(child, module, id, path.Length == 0 ? ScopeName(child) : $"{path}.{ScopeName(child)}", depth + 1);
        foreach (var child in scope.Many(UhdmVpi.VpiGenScope))
            if (!visitedScopes.Contains(child))
                CollectScope(child, module, id, path.Length == 0 ? ScopeName(child) : $"{path}.{ScopeName(child)}", depth + 1);

        return id;
    }

    private bool DeclaredReference(UhdmNode? h) =>
        TypeOf(h) is UhdmVpi.VpiRefObj or UhdmVpi.VpiReg or UhdmVpi.VpiNet or UhdmVpi.VpiBitVar
            or UhdmVpi.VpiVariables or UhdmVpi.VpiEnumVar or UhdmVpi.VpiLogicVar
        || AtomWidth(WordSpec(h)) != 0;

    private void InputEvents(UhdmNode? edge, int scope, Dictionary<string, string> links, List<(string Signal, bool Positive)> events, int depth = 0)
    {
        Need(edge is not null && depth < 16 && edge.VpiType == UhdmVpi.VpiOperation, "explicit edge event required");
        long op = edge!.GetInt(UhdmVpi.VpiOpType);
        var operands = edge.Many(UhdmVpi.VpiOperand);
        if (op == UhdmVpi.VpiEventOrOp)
        {
            foreach (var operand in operands) InputEvents(operand, scope, links, events, depth + 1);
            return;
        }
        Need(op == UhdmVpi.VpiPosedgeOp || op == UhdmVpi.VpiNegedgeOp, "only input posedge/negedge events supported");
        Need(operands.Count == 1 && DeclaredReference(operands[0]), "one declared event input required");
        InScope(scope);
        string root = SignalName(operands[0]);
        var chain = new List<string>();
        while (links.TryGetValue(root, out string? next))
        {
            Need(chain.Count < 256 && !chain.Contains(root), "cyclic event port connection");
            chain.Add(root);
            root = next;
        }
        Need(flatSignals.TryGetValue(root, out var rootSig) && rootSig.Width == 1 && rootSig.Direction == UhdmVpi.VpiInput,
            "event controls must connect directly to a top scalar input");
        foreach (string alias in chain)
        {
            var aliasSig = flatSignals[alias];
            Need(aliasSig.Width == 1 && aliasSig.IsSigned == rootSig!.IsSigned && aliasSig.TwoState == rootSig.TwoState,
                "event aliases require matching signedness");
            clockAliases[alias] = root;
        }
        var ev = (root, op == UhdmVpi.VpiPosedgeOp);
        Need(events.Count < 16 && !events.Contains(ev), "duplicate or excessive process events");
        events.Add(ev);
    }

    private string FlattenDesign(UhdmNode top)
    {
        CollectScope(top, top, -1, string.Empty);
        var bindings = new List<FlatBinding>();
        var inputLinks = new Dictionary<string, string>(StringComparer.Ordinal);

        int initialCount = 0;
        foreach (var scope in flatScopes)
        {
            if (scope.Handle.VpiType == UhdmVpi.VpiModule)
                timedPrecision = Math.Min(timedPrecision, (int)scope.Handle.GetFieldInt("VpiTimePrecision"));
            foreach (var proc in scope.Handle.Many(UhdmVpi.VpiProcess))
                if (proc.VpiType == UhdmVpi.VpiInitial) initialCount++;
        }
        timedLowering |= initialCount > 1;
        foreach (var scope in flatScopes)
        {
            foreach (var proc in scope.Handle.Many(UhdmVpi.VpiProcess))
            {
                if (proc.VpiType == UhdmVpi.VpiAlways)
                {
                    var ev = proc.One(UhdmVpi.VpiStmt);
                    if (TypeOf(ev) == UhdmVpi.VpiEventControl && ev!.One(UhdmVpi.VpiCondition) is null)
                        timedLowering = true;
                }
            }
        }

        if (timedLowering)
        {
            foreach (var scope in flatScopes)
            {
                if (scope.Handle.VpiType != UhdmVpi.VpiModule) continue;
                bool qualified = false;
                foreach (var attr in scope.Handle.Many(UhdmVpi.VpiAttribute))
                    qualified |= attr.Name == "velosim frontend time scale v1";
                Need(qualified, "timed lowering requires preserved explicit timeunit/timeprecision metadata; use the time-scale frontend overlay");
                int unit = (int)scope.Handle.GetFieldInt("VpiTimeUnit");
                int prec = (int)scope.Handle.GetFieldInt("VpiTimePrecision");
                Need(unit is >= -15 and <= 0 && prec >= -15 && prec <= unit, "unsupported HDL time scale");
            }
        }

        for (int index = 0; index < flatScopes.Count; index++)
        {
            var scope = flatScopes[index];
            if (scope.Handle.VpiType != UhdmVpi.VpiModule) continue;
            var expected = expectedPortInitializers.TryGetValue(OwnerDefinition(scope.Handle), out var expSet)
                ? new HashSet<string>(expSet, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in scope.Handle.Many(UhdmVpi.VpiPort))
            {
                if (port.GetInt(UhdmVpi.VpiDirection) != UhdmVpi.VpiOutput) continue;
                string key = InitializerKey(port, scope.Handle);
                if (!originalPortInitializers.TryGetValue(key, out var foundVal)) continue;
                var declaration = port.One(UhdmVpi.VpiLowConn)?.One(UhdmVpi.VpiActual);
                Need(declaration is not null && flatSymbols.ContainsKey(declaration), "output initializer requires a bound declaration");
                Need(expected.Remove(key), "duplicate output initializer binding");
                Need(initializedDeclarations.Add(declaration!), "conflicting output declaration initializers");
                flatInitializers.Add(new FlatInitializer(index, declaration!, foundVal));
            }
            Need(expected.Count == 0, "unresolved output initializer binding");
        }

        for (int index = 1; index < flatScopes.Count; index++)
        {
            var scope = flatScopes[index];
            if (scope.Handle.VpiType != UhdmVpi.VpiModule) continue;
            foreach (var port in scope.Handle.Many(UhdmVpi.VpiPort))
            {
                var high = port.One(UhdmVpi.VpiHighConn);
                var low = port.One(UhdmVpi.VpiLowConn);
                int direction = (int)port.GetInt(UhdmVpi.VpiDirection);
                if (direction == UhdmVpi.VpiOutput && high is null && low is not null) continue;
                Need(high is not null && low is not null, "open/unresolved instance ports unsupported");
                Need(direction == UhdmVpi.VpiInput || direction == UhdmVpi.VpiOutput, "inout instance ports unsupported");
                Need(DeclaredReference(low), "instance low connection must be a declared word");
                InScope(index);
                string child = SignalName(low!);
                Need(flatSignals.TryGetValue(child, out var childSig), "memory instance ports unsupported");
                int childWidth = childSig!.Width;
                InScope(scope.Parent);
                Need(Width(high) >= 1 && childWidth >= 1, "instance connections require word widths");
                if (direction == UhdmVpi.VpiOutput)
                    Need(DeclaredReference(high), "instance output requires a whole declared destination word");
                bindings.Add(new FlatBinding(scope.Parent, index, direction, high!, low!, child));
                if (direction == UhdmVpi.VpiInput && DeclaredReference(high))
                    inputLinks[child] = SignalName(high!);
            }
        }

        var combinational = new List<(int Scope, UhdmNode? Body)>();
        var initial = new List<(int Scope, UhdmNode? Body)>();
        var processes = new List<FlatProcess>();
        var timedProcesses = new List<(int Scope, UhdmNode? Body, bool Repeat)>();
        string clock = string.Empty;
        bool explicitEvents = false;

        for (int index = 0; index < flatScopes.Count; index++)
        {
            foreach (var proc in flatScopes[index].Handle.Many(UhdmVpi.VpiProcess))
            {
                if (timedLowering)
                {
                    Need(proc.VpiType is UhdmVpi.VpiInitial or UhdmVpi.VpiAlways, "unsupported timed process kind");
                    long at = proc.GetInt(UhdmVpi.VpiAlwaysType);
                    if (proc.VpiType == UhdmVpi.VpiAlways && (at == UhdmVpi.VpiAlwaysComb || at == UhdmVpi.VpiAlwaysLatch))
                        combinational.Add((index, proc.One(UhdmVpi.VpiStmt)));
                    else
                        timedProcesses.Add((index, proc.One(UhdmVpi.VpiStmt), proc.VpiType == UhdmVpi.VpiAlways));
                    continue;
                }
                if (proc.VpiType == UhdmVpi.VpiInitial)
                {
                    Need(initial.Count == 0, "multiple initial blocks require time-zero scheduling");
                    initial.Add((index, proc.One(UhdmVpi.VpiStmt)));
                    continue;
                }
                if (proc.VpiType == UhdmVpi.VpiAlways && proc.GetInt(UhdmVpi.VpiAlwaysType) == UhdmVpi.VpiAlwaysComb)
                {
                    Need(combinational.Count < limits.MaxProcesses, "combinational process budget exceeded");
                    combinational.Add((index, proc.One(UhdmVpi.VpiStmt)));
                    continue;
                }
                Need(processes.Count < limits.MaxProcesses, "process budget exceeded");
                long alwaysType = proc.GetInt(UhdmVpi.VpiAlwaysType);
                Need(proc.VpiType == UhdmVpi.VpiAlways && alwaysType != UhdmVpi.VpiAlwaysComb && alwaysType != UhdmVpi.VpiAlwaysLatch,
                    "only explicitly edge-controlled always processes supported");
                var ev = proc.One(UhdmVpi.VpiStmt);
                Need(TypeOf(ev) == UhdmVpi.VpiEventControl, "event control required");
                var lowered = new FlatProcess(index, ev!.One(UhdmVpi.VpiStmt));
                InputEvents(ev.One(UhdmVpi.VpiCondition), index, inputLinks, lowered.Events);
                Need(lowered.Events.Count > 0, "empty event list");
                string primary = lowered.Events[0].Signal;
                if (clock.Length == 0) clock = primary;
                explicitEvents |= lowered.Events.Count != 1 || !lowered.Events[0].Positive || primary != clock;
                processes.Add(lowered);
            }
        }

        var sb = new StringBuilder(4096);
        sb.Append("{\"format\":\"velosim.rtl.v1\",\"signals\":[");
        bool first = true;
        foreach (var (flat, signal) in flatSignals)
        {
            if (clockAliases.ContainsKey(flat)) continue;
            if (!first) sb.Append(',');
            first = false;
            string dirStr = signal.Direction == UhdmVpi.VpiInput ? "input"
                : signal.Direction == UhdmVpi.VpiOutput ? "output" : "internal";
            sb.Append("{\"name\":").Append(Q(flat))
              .Append(",\"width\":").Append(signal.Width)
              .Append(",\"direction\":").Append(Q(dirStr));
            if (signal.IsSigned) sb.Append(",\"signed\":true");
            if (signal.TwoState) sb.Append(",\"two_state\":true");
            if (signal.Path.Length > 0) sb.Append(",\"path\":").Append(Q(signal.Path));
            sb.Append('}');
        }

        sb.Append("],\"memories\":[");
        first = true;
        foreach (var (flat, memory) in flatMemories)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":").Append(Q(flat))
              .Append(",\"width\":").Append(memory.Width)
              .Append(",\"depth\":").Append(memory.Depth);
            if (memory.Lower != 0) sb.Append(",\"lower\":").Append(memory.Lower);
            if (memory.IsSigned) sb.Append(",\"signed\":true");
            if (memory.TwoState) sb.Append(",\"two_state\":true");
            if (memory.Dynamic) sb.Append(",\"dynamic\":true");
            if (memory.Queue) sb.Append(",\"queue\":true");
            if (memory.Associative)
            {
                sb.Append(",\"associative\":true,\"key_width\":").Append(memory.KeyWidth);
                if (memory.KeySigned) sb.Append(",\"key_signed\":true");
            }
            if (memory.Dimensions.Count > 0)
            {
                sb.Append(",\"dimensions\":[");
                for (int i = 0; i < memory.Dimensions.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"left\":").Append(memory.Dimensions[i].Left)
                      .Append(",\"right\":").Append(memory.Dimensions[i].Right).Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
        }

        sb.Append("],\"initialization\":{\"kind\":\"block\",\"items\":[");
        first = true;
        foreach (var init in flatInitializers)
        {
            InScope(init.Scope);
            ValidateInitializerConstant(init.Value, 0);
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"kind\":\"blocking\",\"target\":{\"kind\":\"ref\",\"name\":")
              .Append(Q(SignalName(init.Declaration)))
              .Append("},\"value\":").Append(Expr(init.Value)).Append('}');
        }
        foreach (var (scopeIdx, body) in initial)
        {
            InScope(scopeIdx);
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Stmt(body));
        }

        sb.Append("]},\"continuous\":[");
        first = true;
        void AppendCont(string targetJson, string valueJson)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"target\":").Append(targetJson).Append(",\"value\":").Append(valueJson).Append('}');
        }

        foreach (var binding in bindings)
        {
            if (clockAliases.ContainsKey(binding.Target)) continue;
            InScope(binding.Parent);
            string highJson = Expr(binding.High);
            InScope(binding.Child);
            string lowJson = Expr(binding.Low);
            if (binding.Direction == UhdmVpi.VpiInput) AppendCont(lowJson, highJson);
            else AppendCont(highJson, lowJson);
        }

        for (int index = 0; index < flatScopes.Count; index++)
        {
            InScope(index);
            foreach (var ca in flatScopes[index].Handle.Many(UhdmVpi.VpiContAssign))
            {
                Need(ca.GetFieldRef("Delay") is null && ca.GetFieldInt("VpiStrength0") == 0 && ca.GetFieldInt("VpiStrength1") == 0,
                    "continuous delays and strengths unsupported");
                AppendCont(Expr(ca.One(UhdmVpi.VpiLhs)), Expr(ca.One(UhdmVpi.VpiRhs)));
            }
        }

        sb.Append("],\"combinational\":[");
        first = true;
        foreach (var (scopeIdx, body) in combinational)
        {
            InScope(scopeIdx);
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Stmt(body));
        }

        sb.Append("],\"clock\":").Append(processes.Count == 0 ? "null" : Q(clock)).Append(",\"body\":");
        if (processes.Count > 1) sb.Append("{\"kind\":\"parallel\",\"items\":[");
        first = true;
        foreach (var proc in processes)
        {
            InScope(proc.Scope);
            if (!first) sb.Append(',');
            first = false;
            if (explicitEvents)
            {
                sb.Append("{\"kind\":\"process\",\"events\":[");
                for (int e = 0; e < proc.Events.Count; e++)
                {
                    if (e > 0) sb.Append(',');
                    sb.Append("{\"signal\":").Append(Q(proc.Events[e].Signal))
                      .Append(",\"edge\":").Append(Q(proc.Events[e].Positive ? "posedge" : "negedge")).Append('}');
                }
                sb.Append("],\"body\":");
            }
            sb.Append(Stmt(proc.Body));
            if (explicitEvents) sb.Append('}');
        }
        if (processes.Count == 0) sb.Append("{\"kind\":\"block\",\"items\":[]}");
        if (processes.Count > 1) sb.Append("]}");

        if (timedLowering)
        {
            sb.Append(",\"named_events\":[");
            bool firstNamed = true;
            foreach (string evName in flatNamedEvents)
            {
                if (!firstNamed) sb.Append(',');
                firstNamed = false;
                sb.Append(Q(evName));
            }
            sb.Append("],\"time_precision\":").Append(timedPrecision).Append(",\"timed\":[");
            bool firstTimed = true;
            foreach (var (scopeIdx, body, repeat) in timedProcesses)
            {
                InScope(scopeIdx);
                if (!firstTimed) sb.Append(',');
                firstTimed = false;
                bool implicitStar = repeat && TypeOf(body) == UhdmVpi.VpiEventControl && body!.One(UhdmVpi.VpiCondition) is null;
                if (implicitStar) sb.Append("{\"kind\":\"always_star\",\"body\":");
                else if (repeat) sb.Append("{\"kind\":\"while\",\"condition\":{\"kind\":\"const\",\"bits\":\"1\"},\"body\":");
                sb.Append(Stmt(implicitStar ? body!.One(UhdmVpi.VpiStmt) : body));
                if (repeat) sb.Append('}');
            }
            sb.Append(']');
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private void ValidateInitializerConstant(UhdmNode? h, int depth)
    {
        Need(h is not null && depth < 64, "initializer expression depth exceeded");
        var original = OriginalExpression(h);
        if (original is not null)
        {
            ValidateInitializerConstant(original, depth + 1);
            return;
        }
        if (h!.VpiType == UhdmVpi.VpiConstant || IsParameterReference(h)) return;
        Need(h.VpiType == UhdmVpi.VpiOperation, "declaration initializer requires constant expression");
        foreach (var child in h.Many(UhdmVpi.VpiOperand))
            ValidateInitializerConstant(child, depth + 1);
    }
}
