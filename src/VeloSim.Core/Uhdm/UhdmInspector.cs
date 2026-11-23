using System.Text;
using System.Text.Json;

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

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("format", "velosim.uhdm.inventory.v1");
            w.WriteStartArray("designs");

            int emitted = 0;
            foreach (var design in designs)
            {
                if (design.VpiType != UhdmVpi.VpiDesign) continue;
                emitted++;
                w.WriteStartObject();
                w.WriteString("name", design.Name);
                w.WriteStartArray("top_modules");
                foreach (var mod in design.Many(UhdmVpi.UhdmtopModules))
                    EmitModule(w, mod, 0);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            if (emitted == 0)
                throw new InvalidOperationException("empty UHDM design");

            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void EmitModule(Utf8JsonWriter w, UhdmNode mod, int depth)
    {
        if (depth > MaxModuleDepth)
            throw new InvalidOperationException("module hierarchy exceeds 128 levels");

        w.WriteStartObject();
        w.WriteString("name", mod.Name);
        w.WriteString("definition", mod.DefName);
        w.WriteStartArray("objects");
        EmitCollection(w, mod, UhdmVpi.VpiNet, "nets");
        EmitCollection(w, mod, UhdmVpi.VpiReg, "registers");
        EmitCollection(w, mod, UhdmVpi.VpiVariables, "variables");
        EmitCollection(w, mod, UhdmVpi.VpiProcess, "processes");
        EmitCollection(w, mod, UhdmVpi.VpiContAssign, "continuous_assignments");
        w.WriteEndArray();

        w.WriteStartArray("children");
        foreach (var child in mod.Many(UhdmVpi.VpiModule))
            EmitModule(w, child, depth + 1);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void EmitCollection(Utf8JsonWriter w, UhdmNode parent, int relation, string label)
    {
        foreach (var obj in parent.Many(relation))
        {
            w.WriteStartObject();
            w.WriteNumber("kind", obj.VpiType);
            w.WriteString("name", obj.Name);
            w.WriteString("file", obj.File);
            w.WriteNumber("line", obj.LineNo);
            w.WriteNumber("width", obj.GetInt(UhdmVpi.VpiSize));
            w.WriteString("collection", label);
            w.WriteEndObject();
        }
    }
}
