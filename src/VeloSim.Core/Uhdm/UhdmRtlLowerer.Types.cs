namespace VeloSim.Uhdm;

public sealed partial class UhdmRtlLowerer
{
    internal bool UnionTagProvenance { get; set; }
    internal int PackedQueryActive { get; private set; }
    private int packedQueryRemaining;

    private void PackedQueryStep()
    {
        Need(packedQueryRemaining > 0, "packed type traversal budget exceeded");
        packedQueryRemaining--;
    }

    private PackedQueryScope BeginPackedQuery()
    {
        if (PackedQueryActive == 0) packedQueryRemaining = 20_000;
        PackedQueryStep();
        PackedQueryActive++;
        return new PackedQueryScope(this);
    }

    private readonly struct PackedQueryScope(UhdmRtlLowerer owner) : IDisposable
    {
        public void Dispose() => owner.PackedQueryActive--;
    }

    internal readonly record struct PackedField(string Root, int Offset, int Width, bool Sign, bool TwoState);

    internal int AggregateWidth(UhdmNode? spec, int depth)
    {
        using var query = BeginPackedQuery();
        Need(depth < 32 && AggregateSpec(spec) && spec!.GetBool(UhdmVpi.VpiPacked),
            "only bounded packed structs/unions supported");

        if (spec!.VpiType == UhdmVpi.VpiUnionTypespec)
        {
            Need(UnionTagProvenance, "union tag provenance requires patched frontend");
            Need(!spec.GetFieldBool("VpiTagged"), "tagged unions require tag-aware storage");
        }

        var members = spec.Many(UhdmVpi.VpiTypespecMember);
        Need(members.Count > 0 && members.Count <= 256, "packed aggregate member budget exceeded");

        int total = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            PackedQueryStep();
            Need(names.Add(member.Name), "duplicate packed member");
            int w = Width(member, depth + 1);
            if (spec.VpiType == UhdmVpi.VpiUnionTypespec)
            {
                Need(total == 0 || total == w, "packed union members require equal widths");
                total = w;
            }
            else
            {
                total += w;
            }
            Need(total <= limits.MaxWordBits, "packed aggregate exceeds word width limit");
        }
        return total;
    }

    internal bool AggregateState(UhdmNode? spec, int depth)
    {
        using var query = BeginPackedQuery();
        Need(depth < 32, "packed aggregate type depth exceeded");
        Need(spec is not null, "packed aggregate requires members");
        var members = spec!.Many(UhdmVpi.VpiTypespecMember);
        Need(members.Count > 0, "packed aggregate requires members");
        bool? state = null;
        foreach (var member in members)
        {
            PackedQueryStep();
            var child = WordSpec(member);
            bool two = AggregateSpec(child) ? AggregateState(child, depth + 1) : TwoStateWord(member);
            Need(!state.HasValue || state.Value == two, "mixed two/four-state packed members unsupported");
            state = two;
        }
        return state!.Value;
    }

    private PackedField ResolvePackedField(UhdmNode h)
    {
        Need(!insideCall, "packed subroutine member paths unsupported");
        var elements = h.GetFieldList("Path_elems");
        Need(elements.Count is >= 2 and <= 16, "packed field path requires2..16 elements");

        var root = elements[0];
        var declaration = root.One(UhdmVpi.VpiActual);
        Need(declaration is not null && flatSymbols.ContainsKey(declaration),
            "packed field root must bind a collected declaration");
        var spec = WordSpec(declaration);
        Need(AggregateSpec(spec), "hierarchical non-aggregate paths unsupported");

        string rootName = SignalName(root);
        int fieldOffset = 0;
        int fieldWidth = Width(declaration);
        bool fieldSign = false;
        bool fieldTwoState = false;

        for (int i = 1; i < elements.Count; i++)
        {
            var part = elements[i];
            Need(part.VpiType == UhdmVpi.VpiRefObj && TypeOf(part.One(UhdmVpi.VpiActual)) == UhdmVpi.VpiTypespecMember,
                "only whole packed member paths supported");
            var members = spec!.Many(UhdmVpi.VpiTypespecMember);
            int offset = AggregateWidth(spec, 0);
            UhdmNode? selected = null;
            foreach (var member in members)
            {
                int w = Width(member);
                if (spec.VpiType != UhdmVpi.VpiUnionTypespec) offset -= w;
                if (member.Name == part.Name)
                {
                    Need(selected is null, "ambiguous packed member");
                    selected = member;
                    fieldOffset += spec.VpiType == UhdmVpi.VpiUnionTypespec ? 0 : offset;
                    fieldWidth = w;
                }
            }
            Need(selected is not null, "member missing from bound packed type");
            var actual = part.One(UhdmVpi.VpiActual)!;
            Need(ReferenceEquals(actual, selected) ||
                (LexicalKey(selected).Length > 0 && LexicalKey(actual) == LexicalKey(selected) && Width(actual) == Width(selected)),
                "packed member must bind its typed declaration");
            fieldSign = DeclaredSigned(selected!);
            fieldTwoState = TwoStateWord(selected!);
            spec = WordSpec(selected!);
            if (i + 1 < elements.Count)
                Need(AggregateSpec(spec), "intermediate packed member is not an aggregate");
        }

        return new PackedField(rootName, fieldOffset, fieldWidth, fieldSign, fieldTwoState);
    }

    private string PackedFieldExpression(UhdmNode h, bool target)
    {
        var field = ResolvePackedField(h);
        string selected = $"{{\"kind\":\"partselect\",\"value\":{{\"kind\":\"ref\",\"name\":{Q(field.Root)}}},\"left\":{field.Offset + field.Width - 1},\"right\":{field.Offset}}}";
        if (target) return selected;
        return $"{{\"kind\":\"convert\",\"value\":{selected},\"width\":{field.Width},\"signed\":{(field.Sign ? "true" : "false")},\"two_state\":{(field.TwoState ? "true" : "false")}}}";
    }
}
