using System.Globalization;
using System.Text;

namespace VeloSim.Uhdm;

/// <summary>
/// Pure .NET 11 port of <c>native/uhdm/inspect.cpp</c>.
/// Restores a binary UHDM database and emits the canonical <c>velosim.uhdm.inventory.v1</c> JSON inventory.
/// </summary>
public static class UhdmInspector
{
    public const int MaxModuleDepth = 128;

    public static string InspectFile(string uhdmPath)
    {
        var db = UhdmDatabase.Load(uhdmPath);
        return Inspect(db);
    }

    public static string Inspect(UhdmDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var designs = database.Designs;
        if (designs.Count == 0)
            throw new InvalidOperationException("empty UHDM design");

        var sb = new StringBuilder(1024);
        sb.Append("{\"format\":\"velosim.uhdm.inventory.v1\",\"designs\":[");

        int emitted = 0;
        foreach (var design in designs)
        {
            if (design.VpiType != UhdmVpi.VpiDesign) continue;
            if (emitted > 0) sb.Append(',');
            emitted++;
            sb.Append("{\"name\":");
            UhdmJson.AppendEscaped(sb, design.Name);
            sb.Append(",\"top_modules\":[");
            bool firstMod = true;
            foreach (var mod in design.Many(UhdmVpi.UhdmtopModules))
            {
                if (!firstMod) sb.Append(',');
                firstMod = false;
                EmitModule(sb, mod, 0);
            }
            sb.Append("]}");
        }

        if (emitted == 0)
            throw new InvalidOperationException("empty UHDM design");

        sb.Append("]}\n");
        return sb.ToString();
    }

    private static void EmitModule(StringBuilder sb, UhdmNode mod, int depth)
    {
        if (depth > MaxModuleDepth)
            throw new InvalidOperationException("module hierarchy exceeds 128 levels");

        sb.Append("{\"name\":");
        UhdmJson.AppendEscaped(sb, mod.Name);
        sb.Append(",\"definition\":");
        UhdmJson.AppendEscaped(sb, mod.DefName);
        sb.Append(",\"objects\":[");
        bool firstObj = true;
        EmitCollection(sb, mod, UhdmVpi.VpiNet, "nets", ref firstObj);
        EmitCollection(sb, mod, UhdmVpi.VpiReg, "registers", ref firstObj);
        EmitCollection(sb, mod, UhdmVpi.VpiVariables, "variables", ref firstObj);
        EmitCollection(sb, mod, UhdmVpi.VpiProcess, "processes", ref firstObj);
        EmitCollection(sb, mod, UhdmVpi.VpiContAssign, "continuous_assignments", ref firstObj);
        sb.Append("],\"children\":[");

        bool firstChild = true;
        foreach (var child in mod.Many(UhdmVpi.VpiModule))
        {
            if (!firstChild) sb.Append(',');
            firstChild = false;
            EmitModule(sb, child, depth + 1);
        }
        sb.Append("]}");
    }

    private static void EmitCollection(StringBuilder sb, UhdmNode parent, int relation, string label, ref bool firstObj)
    {
        foreach (var obj in parent.Many(relation))
        {
            if (!firstObj) sb.Append(',');
            firstObj = false;
            sb.Append("{\"kind\":")
              .Append(obj.VpiType.ToString(CultureInfo.InvariantCulture))
              .Append(",\"name\":");
            UhdmJson.AppendEscaped(sb, obj.Name);
            sb.Append(",\"file\":");
            UhdmJson.AppendEscaped(sb, obj.File);
            sb.Append(",\"line\":")
              .Append(obj.LineNo.ToString(CultureInfo.InvariantCulture))
              .Append(",\"width\":")
              .Append(obj.GetInt(UhdmVpi.VpiSize).ToString(CultureInfo.InvariantCulture))
              .Append(",\"collection\":");
            UhdmJson.AppendEscaped(sb, label);
            sb.Append('}');
        }
    }
}
