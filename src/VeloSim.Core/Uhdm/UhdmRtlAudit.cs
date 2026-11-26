using System.Globalization;

namespace VeloSim.Uhdm;

/// <summary>
/// Pure .NET 11 port of <c>native/uhdm/rtl_audit.h</c> (<c>velosim::RtlExecutableAudit</c>).
/// Performs a bounded, cycle-safe traversal of ALL UHDM relations (matching <c>UHDM::UhdmListener</c>)
/// and fails closed on unsupported executable constructs before RTL lowering begins.
/// </summary>
public sealed class UhdmRtlAudit
{
    private readonly Dictionary<int, int> counts = [];
    private readonly HashSet<UhdmNode> visited = new(ReferenceEqualityComparer.Instance);
    private readonly int nodeBudget;
    private readonly int depthBudget;
    private int count;

    public UhdmRtlAudit(int nodeBudget = 100_000, int depthBudget = 128)
    {
        if (nodeBudget is <= 0 or > 2_000_000 || depthBudget is <= 0 or > 256)
            throw new ArgumentException("invalid UHDM executable audit budget");
        this.nodeBudget = nodeBudget;
        this.depthBudget = depthBudget;
    }

    public int Count => count;
    public IReadOnlyDictionary<int, int> Counts => counts;

    public void Audit(UhdmNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Visit(root, 0);
    }

    private void Visit(UhdmNode node, int depth)
    {
        // Shared declarations are visited once, matching UHDM::UhdmListener's visited set.
        if (!visited.Add(node))
            return;

        if (++count > nodeBudget || depth + 1 > depthBudget)
            throw new InvalidOperationException("UHDM executable audit traversal budget exceeded");

        counts[node.VpiType] = counts.GetValueOrDefault(node.VpiType) + 1;

        switch (node.ObjectType)
        {
            case UhdmObjectType.AssertStmt: Unsupported(node, "concurrent assertion"); break;
            case UhdmObjectType.Assume: Unsupported(node, "concurrent assumption"); break;
            case UhdmObjectType.Cover: Unsupported(node, "concurrent cover"); break;
            case UhdmObjectType.ImmediateAssert: Unsupported(node, "immediate assertion"); break;
            case UhdmObjectType.ImmediateAssume: Unsupported(node, "immediate assumption"); break;
            case UhdmObjectType.ImmediateCover: Unsupported(node, "immediate cover"); break;
            case UhdmObjectType.FinalStmt: Unsupported(node, "final process"); break;
            case UhdmObjectType.ClockingBlock: Unsupported(node, "clocking block"); break;
            case UhdmObjectType.Program: Unsupported(node, "program"); break;
            case UhdmObjectType.InterfaceInst: Unsupported(node, "interface instance"); break;
        }

        // Match UHDM::UhdmListener relation traversal order for Primitives / Primitive_arrays without extra lookups.
        foreach (var (fieldName, child) in node.EnumerateAllRelations())
        {
            if (fieldName == "Primitives")
                Unsupported(child, "primitive instance");
            else if (fieldName == "Primitive_arrays")
                Unsupported(child, "primitive array");
            Visit(child, depth + 1);
        }
    }

    private static void Unsupported(UhdmNode node, string construct)
    {
        string file = string.Empty;
        UhdmNode? owner = node;
        for (int depth = 0; owner is not null && depth < 128; depth++)
        {
            file = owner.File;
            if (file.Length > 0) break;
            var parent = owner.Parent;
            if (ReferenceEquals(parent, owner)) break;
            owner = parent;
        }
        if (file.Length == 0) file = "<unknown source>";
        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
            $"unsupported executable UHDM construct: {construct} at {file}:{node.LineNo}:{node.ColumnNo}"));
    }
}
