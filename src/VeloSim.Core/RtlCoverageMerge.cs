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

    private sealed class CoverBinAcc
    {
        public required string Name { get; init; }
        public ulong Lo { get; init; }
        public ulong Hi { get; init; }
        public bool Default { get; init; }
        public bool Ignore { get; init; }
        public bool Illegal { get; init; }
        public ulong Hits { get; set; }
    }

    private sealed class CoverpointAcc
    {
        public required string Name { get; init; }
        public List<CoverBinAcc> Bins { get; } = [];
    }

    private sealed class CrossBinAcc
    {
        public required string A { get; init; }
        public required string B { get; init; }
        public ulong Hits { get; set; }
    }

    private sealed class CrossAcc
    {
        public required string Name { get; init; }
        public required string CpA { get; init; }
        public required string CpB { get; init; }
        public List<CrossBinAcc> Bins { get; } = [];
    }

    private sealed class CovergroupAcc
    {
        public required string Name { get; init; }
        public ulong Samples { get; set; }
        public List<CoverpointAcc> Coverpoints { get; } = [];
        public List<CrossAcc> Crosses { get; } = [];
    }

    private sealed class PropertyAcc
    {
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public ulong Passes { get; set; }
        public ulong Failures { get; set; }
        public ulong Vacuous { get; set; }
    }

    public static string MergeFiles(string outputPath, IReadOnlyList<string> inputPaths)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Coverage merge output path is required.", nameof(outputPath));
        if (inputPaths is null || inputPaths.Count == 0) throw new ArgumentException("At least one input coverage file is required.", nameof(inputPaths));

        string? modelName = null;
        var points = new Dictionary<(string Id, string Kind), PointAcc>();
        var pointOrder = new List<(string Id, string Kind)>();
        Dictionary<string, ToggleAcc> toggles = [with(StringComparer.Ordinal)];
        var toggleOrder = new List<string>();
        Dictionary<string, CovergroupAcc> covergroups = [with(StringComparer.Ordinal)];
        var covergroupOrder = new List<string>();
        Dictionary<string, PropertyAcc> properties = [with(StringComparer.Ordinal)];
        var propertyOrder = new List<string>();

        int fileIndex = 0;
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

            if (root.TryGetProperty("covergroups", out var cgsEl))
            {
                HashSet<string> seenCgs = [with(StringComparer.Ordinal)];
                foreach (var cgEl in cgsEl.EnumerateArray())
                {
                    string cgName = cgEl.GetProperty("name").GetString() ?? "";
                    seenCgs.Add(cgName);
                    ulong samples = cgEl.GetProperty("samples").GetUInt64();
                    var cpsArr = cgEl.GetProperty("coverpoints").EnumerateArray().ToArray();
                    var crsArr = cgEl.TryGetProperty("crosses", out var crProp) ? crProp.EnumerateArray().ToArray() : [];
                    if (!covergroups.TryGetValue(cgName, out var cgAcc))
                    {
                        if (fileIndex > 0)
                            throw new InvalidOperationException($"Covergroup '{cgName}' missing from baseline coverage file.");
                        cgAcc = new CovergroupAcc { Name = cgName, Samples = samples };
                        foreach (var cpEl in cpsArr)
                        {
                            var cpAcc = new CoverpointAcc { Name = cpEl.GetProperty("name").GetString() ?? "" };
                            foreach (var bEl in cpEl.GetProperty("bins").EnumerateArray())
                            {
                                cpAcc.Bins.Add(new CoverBinAcc
                                {
                                    Name = bEl.GetProperty("name").GetString() ?? "",
                                    Lo = bEl.GetProperty("lo").GetUInt64(),
                                    Hi = bEl.GetProperty("hi").GetUInt64(),
                                    Default = bEl.GetProperty("default").GetBoolean(),
                                    Ignore = bEl.GetProperty("ignore").GetBoolean(),
                                    Illegal = bEl.GetProperty("illegal").GetBoolean(),
                                    Hits = bEl.GetProperty("hits").GetUInt64()
                                });
                            }
                            cgAcc.Coverpoints.Add(cpAcc);
                        }
                        foreach (var crEl in crsArr)
                        {
                            var crAcc = new CrossAcc
                            {
                                Name = crEl.GetProperty("name").GetString() ?? "",
                                CpA = crEl.GetProperty("cp_a").GetString() ?? "",
                                CpB = crEl.GetProperty("cp_b").GetString() ?? ""
                            };
                            foreach (var cbEl in crEl.GetProperty("bins").EnumerateArray())
                            {
                                crAcc.Bins.Add(new CrossBinAcc
                                {
                                    A = cbEl.GetProperty("a").GetString() ?? "",
                                    B = cbEl.GetProperty("b").GetString() ?? "",
                                    Hits = cbEl.GetProperty("hits").GetUInt64()
                                });
                            }
                            cgAcc.Crosses.Add(crAcc);
                        }
                        covergroups.Add(cgName, cgAcc);
                        covergroupOrder.Add(cgName);
                    }
                    else
                    {
                        if (cgAcc.Coverpoints.Count != cpsArr.Length || cgAcc.Crosses.Count != crsArr.Length)
                            throw new InvalidOperationException($"Covergroup '{cgName}' schema mismatch across runs.");
                        cgAcc.Samples = checked(cgAcc.Samples + samples);
                        for (int pi = 0; pi < cpsArr.Length; ++pi)
                        {
                            var cpAcc = cgAcc.Coverpoints[pi];
                            var binsArr = cpsArr[pi].GetProperty("bins").EnumerateArray().ToArray();
                            if (cpAcc.Name != (cpsArr[pi].GetProperty("name").GetString() ?? "") || cpAcc.Bins.Count != binsArr.Length)
                                throw new InvalidOperationException($"Coverpoint '{cpAcc.Name}' schema mismatch in covergroup '{cgName}'.");
                            for (int bi = 0; bi < binsArr.Length; ++bi)
                            {
                                if (cpAcc.Bins[bi].Name != (binsArr[bi].GetProperty("name").GetString() ?? ""))
                                    throw new InvalidOperationException($"Coverpoint bin mismatch in '{cgName}.{cpAcc.Name}'.");
                                cpAcc.Bins[bi].Hits = checked(cpAcc.Bins[bi].Hits + binsArr[bi].GetProperty("hits").GetUInt64());
                            }
                        }
                        for (int ci = 0; ci < crsArr.Length; ++ci)
                        {
                            var crAcc = cgAcc.Crosses[ci];
                            var cbArr = crsArr[ci].GetProperty("bins").EnumerateArray().ToArray();
                            if (crAcc.Name != (crsArr[ci].GetProperty("name").GetString() ?? "") || crAcc.Bins.Count != cbArr.Length)
                                throw new InvalidOperationException($"Cross '{crAcc.Name}' schema mismatch in covergroup '{cgName}'.");
                            for (int bi = 0; bi < cbArr.Length; ++bi)
                                crAcc.Bins[bi].Hits = checked(crAcc.Bins[bi].Hits + cbArr[bi].GetProperty("hits").GetUInt64());
                        }
                    }
                }
                if (fileIndex > 0 && seenCgs.Count != covergroups.Count)
                    throw new InvalidOperationException($"Covergroup count mismatch in {inputPath}.");
            }
            else if (fileIndex > 0 && covergroups.Count > 0)
            {
                throw new InvalidOperationException($"Covergroups missing from {inputPath}.");
            }

            if (root.TryGetProperty("properties", out var propsEl))
            {
                foreach (var pEl in propsEl.EnumerateArray())
                {
                    string pName = pEl.GetProperty("name").GetString() ?? "";
                    string pKind = pEl.GetProperty("kind").GetString() ?? "";
                    ulong passes = pEl.GetProperty("passes").GetUInt64();
                    ulong failures = pEl.GetProperty("failures").GetUInt64();
                    ulong vacuous = pEl.GetProperty("vacuous").GetUInt64();
                    if (!properties.TryGetValue(pName, out var acc))
                    {
                        acc = new PropertyAcc { Name = pName, Kind = pKind, Passes = passes, Failures = failures, Vacuous = vacuous };
                        properties.Add(pName, acc);
                        propertyOrder.Add(pName);
                    }
                    else
                    {
                        if (!string.Equals(acc.Kind, pKind, StringComparison.Ordinal))
                            throw new InvalidOperationException($"Property '{pName}' kind mismatch: '{acc.Kind}' vs '{pKind}'.");
                        acc.Passes = checked(acc.Passes + passes);
                        acc.Failures = checked(acc.Failures + failures);
                        acc.Vacuous = checked(acc.Vacuous + vacuous);
                    }
                }
            }
            ++fileIndex;
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
            if (covergroupOrder.Count > 0)
            {
                w.WriteStartArray("covergroups");
                foreach (var cgName in covergroupOrder)
                {
                    var cg = covergroups[cgName];
                    w.WriteStartObject();
                    w.WriteString("name", cg.Name);
                    w.WriteNumber("samples", cg.Samples);
                    w.WriteStartArray("coverpoints");
                    foreach (var cp in cg.Coverpoints)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", cp.Name);
                        w.WriteStartArray("bins");
                        foreach (var b in cp.Bins)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", b.Name);
                            w.WriteNumber("lo", b.Lo);
                            w.WriteNumber("hi", b.Hi);
                            w.WriteBoolean("default", b.Default);
                            w.WriteBoolean("ignore", b.Ignore);
                            w.WriteBoolean("illegal", b.Illegal);
                            w.WriteNumber("hits", b.Hits);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("crosses");
                    foreach (var cr in cg.Crosses)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", cr.Name);
                        w.WriteString("cp_a", cr.CpA);
                        w.WriteString("cp_b", cr.CpB);
                        w.WriteStartArray("bins");
                        foreach (var cb in cr.Bins)
                        {
                            w.WriteStartObject();
                            w.WriteString("a", cb.A);
                            w.WriteString("b", cb.B);
                            w.WriteNumber("hits", cb.Hits);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (propertyOrder.Count > 0)
            {
                w.WriteStartArray("properties");
                foreach (var pName in propertyOrder)
                {
                    var p = properties[pName];
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    w.WriteString("kind", p.Kind);
                    w.WriteNumber("passes", p.Passes);
                    w.WriteNumber("failures", p.Failures);
                    w.WriteNumber("vacuous", p.Vacuous);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
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
