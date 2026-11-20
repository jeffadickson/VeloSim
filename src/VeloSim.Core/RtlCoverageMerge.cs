using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace VeloSim;

/// <summary>
/// Merges multiple velosim.dut.coverage.v1 JSON files produced by separate DUT runs.
/// Sums branch/case hit counts and bitwise-ORs per-signal toggle masks across runs,
/// failing closed on schema, model name, or signal-width mismatches.
/// </summary>
public static class RtlCoverageMerge
{
    public const string Schema = "velosim.dut.coverage.v1";

    private sealed class PointAcc
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public ulong Hits { get; set; }
    }

    private sealed class ToggleAcc
    {
        public required string Signal { get; init; }
        public required int Width { get; init; }
        public BigInteger Rise { get; set; }
        public BigInteger Fall { get; set; }
    }

    public static string MergeFiles(string outputPath, IReadOnlyList<string> inputPaths)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Coverage merge output path is required.", nameof(outputPath));
        if (inputPaths is null || inputPaths.Count == 0) throw new ArgumentException("At least one input coverage file is required.", nameof(inputPaths));

        string? modelName = null;
        var points = new Dictionary<(string Id, string Kind), PointAcc>();
        var pointOrder = new List<(string Id, string Kind)>();
        var toggles = new Dictionary<string, ToggleAcc>(StringComparer.Ordinal);
        var toggleOrder = new List<string>();

        foreach (string inputPath in inputPaths)
        {
            if (!File.Exists(inputPath)) throw new FileNotFoundException("Coverage input file not found.", inputPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(inputPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var schemaEl) || schemaEl.GetString() != Schema)
                throw new InvalidOperationException($"Unsupported coverage schema in {inputPath} (expected {Schema}).");
            string fileModel = root.GetProperty("model").GetString()
                ?? throw new InvalidOperationException($"Missing model name in {inputPath}.");
            if (modelName is null) modelName = fileModel;
            else if (!string.Equals(modelName, fileModel, StringComparison.Ordinal))
                throw new InvalidOperationException($"Cannot merge coverage from different models: '{modelName}' vs '{fileModel}'.");

            if (root.TryGetProperty("points", out var ptsEl))
            {
                foreach (var p in ptsEl.EnumerateArray())
                {
                    string id = p.GetProperty("id").GetString() ?? "";
                    string kind = p.GetProperty("kind").GetString() ?? "";
                    ulong hits = p.GetProperty("hits").GetUInt64();
                    var key = (id, kind);
                    if (!points.TryGetValue(key, out var acc))
                    {
                        acc = new PointAcc { Id = id, Kind = kind, Hits = 0 };
                        points.Add(key, acc);
                        pointOrder.Add(key);
                    }
                    acc.Hits = checked(acc.Hits + hits);
                }
            }

            if (root.TryGetProperty("toggles", out var togsEl))
            {
                foreach (var t in togsEl.EnumerateArray())
                {
                    string sig = t.GetProperty("signal").GetString() ?? "";
                    int width = t.GetProperty("width").GetInt32();
                    BigInteger rise = ParseHexUnsigned(t.GetProperty("rise_hex").GetString() ?? "0");
                    BigInteger fall = ParseHexUnsigned(t.GetProperty("fall_hex").GetString() ?? "0");
                    if (!toggles.TryGetValue(sig, out var acc))
                    {
                        acc = new ToggleAcc { Signal = sig, Width = width, Rise = rise, Fall = fall };
                        toggles.Add(sig, acc);
                        toggleOrder.Add(sig);
                    }
                    else
                    {
                        if (acc.Width != width)
                            throw new InvalidOperationException($"Toggle signal '{sig}' width mismatch: {acc.Width} vs {width}.");
                        acc.Rise |= rise;
                        acc.Fall |= fall;
                    }
                }
            }
        }

        ulong covered = 0;
        foreach (var k in pointOrder) if (points[k].Hits > 0) ++covered;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("model", modelName ?? "");
            w.WriteNumber("merged_runs", inputPaths.Count);
            w.WriteNumber("total", pointOrder.Count);
            w.WriteNumber("covered", covered);
            w.WriteStartArray("points");
            foreach (var k in pointOrder)
            {
                var p = points[k];
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("kind", p.Kind);
                w.WriteNumber("hits", p.Hits);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("toggles");
            foreach (var sig in toggleOrder)
            {
                var t = toggles[sig];
                w.WriteStartObject();
                w.WriteString("signal", t.Signal);
                w.WriteNumber("width", t.Width);
                w.WriteString("rise_hex", FormatHexFixed(t.Rise, t.Width));
                w.WriteString("fall_hex", FormatHexFixed(t.Fall, t.Width));
                w.WriteNumber("rise_hits", PopCount(t.Rise));
                w.WriteNumber("fall_hits", PopCount(t.Fall));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        string fullOut = Path.GetFullPath(outputPath);
        string? dir = Path.GetDirectoryName(fullOut);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullOut, Encoding.UTF8.GetString(ms.ToArray()) + "\n", new UTF8Encoding(false));
        return fullOut;
    }

    private static BigInteger ParseHexUnsigned(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return BigInteger.Zero;
        return BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static string FormatHexFixed(BigInteger value, int width)
    {
        int nibbles = Math.Max(1, (width + 3) / 4);
        string raw = value.ToString("x", CultureInfo.InvariantCulture);
        if (raw.Length > nibbles && raw.StartsWith('0')) raw = raw[^nibbles..];
        return raw.Length < nibbles ? raw.PadLeft(nibbles, '0') : raw;
    }

    private static ulong PopCount(BigInteger value)
    {
        ulong count = 0;
        foreach (byte b in value.ToByteArray(isUnsigned: true, isBigEndian: false))
            count += (ulong)BitOperations.PopCount(b);
        return count;
    }
}
