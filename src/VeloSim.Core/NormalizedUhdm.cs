using System.Text.Json;

namespace VeloSim;

public static class NormalizedUhdm
{
    public static IrDesign Lower(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonShape.Fields(root, ["format", "signals", "processes"]);
            Require(root.GetProperty("format").GetString() == "velosim.uhdm.normalized.v1", "Unsupported normalized UHDM.");
            var sigs = new List<IrSignal>();
            foreach (var s in root.GetProperty("signals").EnumerateArray())
            {
                JsonShape.Fields(s, ["name", "width"]);
                int width = s.GetProperty("width").GetInt32();
                sigs.Add(new(s.GetProperty("name").GetString()!, new(width, 0, width == 64 ? ulong.MaxValue : width is >= 1 and <= 63 ? (1UL << width) - 1 : 0)));
            }
            var names = new IrDesign(sigs, []).Signals.ToDictionary(s => s.Name, s => s.Initial.Width, StringComparer.Ordinal);
            var processes = new List<IrProcess>();
            foreach (var p in root.GetProperty("processes").EnumerateArray())
            {
                JsonShape.Fields(p, ["kind", "event", "body"]); Kind(p, "always_ff");
                var e = p.GetProperty("event"); JsonShape.Fields(e, ["edge", "signal"]);
                var clock = e.GetProperty("signal").GetString()!;
                Require(names.GetValueOrDefault(clock) == 1, "Clock must be scalar.");
                var edge = e.GetProperty("edge").GetString() switch { "posedge" => IrEdge.Posedge, "negedge" => IrEdge.Negedge, _ => throw new IrValidationException("Unsupported event edge.") };
                var body = p.GetProperty("body"); JsonShape.Fields(body, ["kind", "condition", "then", "else"]); Kind(body, "if");
                var c = body.GetProperty("condition"); JsonShape.Fields(c, ["kind", "operand"]); Kind(c, "not");
                var operand = c.GetProperty("operand"); JsonShape.Fields(operand, ["kind", "name"]); Kind(operand, "ref");
                var reset = operand.GetProperty("name").GetString()!;
                Require(names.GetValueOrDefault(reset) == 1, "Reset must be scalar.");
                var yes = body.GetProperty("then"); var no = body.GetProperty("else");
                foreach (var branch in new[] { yes, no }) { JsonShape.Fields(branch, ["kind", "target", "value"]); Kind(branch, "nba"); }
                var dst = yes.GetProperty("target").GetString()!;
                Require(names.ContainsKey(dst) && dst == no.GetProperty("target").GetString(), "Branch destinations differ or unknown.");
                Constant(yes.GetProperty("value"), 0);
                var add = no.GetProperty("value"); JsonShape.Fields(add, ["kind", "lhs", "rhs"]); Kind(add, "add");
                var lhs = add.GetProperty("lhs"); JsonShape.Fields(lhs, ["kind", "name"]); Kind(lhs, "ref");
                Require(lhs.GetProperty("name").GetString() == dst, "Increment must reference destination."); Constant(add.GetProperty("rhs"), 1);
                processes.Add(new(clock, dst, dst, reset, edge, IrOperation.SvIfResetIncrementStrict));
            }
            Require(processes.Count > 0, "Missing processes.");
            return new(sigs, processes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException)
        { throw new IrValidationException("Malformed normalized UHDM: " + ex.Message); }
    }
    private static void Constant(JsonElement e, int expected)
    { JsonShape.Fields(e, ["kind", "value"]); Kind(e, "const"); Require(e.GetProperty("value").GetInt32() == expected, "Unsupported constant."); }
    private static void Kind(JsonElement e, string kind) => Require(e.GetProperty("kind").GetString() == kind, "Expected " + kind + ".");
    private static void Require(bool condition, string message) { if (!condition) throw new IrValidationException(message); }
}
