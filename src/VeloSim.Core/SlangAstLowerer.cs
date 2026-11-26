using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloSim;

/// <summary>
/// Direct SystemVerilog -> velosim.rtl.v1 lowerer backed by MikePopoloski/slang (MIT).
/// Bypasses Surelog, UHDM, and Python completely by lowering slang's elaborated,
/// type-checked, parameter-resolved AST JSON directly into validated VeloSim RTL IR.
/// </summary>
public static partial class SlangAstLowerer
{
    private sealed record SigDecl(string Name, int Width, string Direction, bool Signed, bool TwoState);
    private sealed record MemDecl(string Name, int Width, int Depth, bool Signed, int Lower, bool TwoState);

    public static async Task<string> LowerSystemVerilogAsync(
        IReadOnlyList<string> sourceFiles,
        string top,
        string? slangExecutable = null,
        CancellationToken token = default)
    {
        if (sourceFiles.Count == 0)
            throw new ArgumentException("At least one SystemVerilog source file is required.", nameof(sourceFiles));
        if (string.IsNullOrWhiteSpace(top))
            throw new ArgumentException("Top module name is required.", nameof(top));

        string slang = ResolveSlangExecutable(slangExecutable);
        string tmpJson = Path.Combine(Path.GetTempPath(), $"velosim-slang-{Guid.NewGuid():N}.ast.json");
        try
        {
            var psi = new ProcessStartInfo(slang)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--std");
            psi.ArgumentList.Add("1800-2023");
            psi.ArgumentList.Add("--top");
            psi.ArgumentList.Add(top);
            psi.ArgumentList.Add("--ast-json");
            psi.ArgumentList.Add(tmpJson);
            foreach (string src in sourceFiles)
                psi.ArgumentList.Add(Path.GetFullPath(src));

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch slang executable: {slang}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
            var stderrTask = proc.StandardError.ReadToEndAsync(token);
            await proc.WaitForExitAsync(token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"slang elaboration failed (exit {proc.ExitCode}): {(stderr + "\n" + stdout).Trim()}");
            if (!File.Exists(tmpJson))
                throw new InvalidOperationException("slang completed without producing AST JSON.");

            string astJson = await File.ReadAllTextAsync(tmpJson, token).ConfigureAwait(false);
            return LowerAstJson(astJson, top);
        }
        finally
        {
            if (File.Exists(tmpJson)) File.Delete(tmpJson);
        }
    }

    public static string LowerAstJson(string astJson, string? topModule = null)
    {
        using var doc = JsonDocument.Parse(astJson);
        if (!doc.RootElement.TryGetProperty("design", out var designEl))
            throw new ArgumentException("Invalid slang AST JSON: missing 'design' root.");

        JsonElement? topInst = FindTopInstance(designEl, topModule);
        if (topInst is null)
            throw new ArgumentException($"Top module instance '{topModule ?? "<any>"}' not found in slang AST JSON.");

        var bodyEl = topInst.Value.GetProperty("body");
        string topName = bodyEl.GetProperty("name").GetString()!;

        var portDirs = new Dictionary<string, string>(StringComparer.Ordinal);
        var signals = new Dictionary<string, SigDecl>(StringComparer.Ordinal);
        var signalOrder = new List<string>();
        var memories = new Dictionary<string, MemDecl>(StringComparer.Ordinal);
        var memOrder = new List<string>();
        var continuousNodes = new List<JsonElement>();
        var processNodes = new List<JsonElement>();

        if (bodyEl.TryGetProperty("members", out var members))
        {
            foreach (var m in members.EnumerateArray())
            {
                string kind = m.GetProperty("kind").GetString()!;
                if (kind == "Port")
                {
                    string pname = m.GetProperty("name").GetString()!;
                    string dir = m.GetProperty("direction").GetString()! switch
                    {
                        "In" => "input",
                        "Out" => "output",
                        _ => throw new NotSupportedException($"Unsupported port direction on '{pname}': {m.GetProperty("direction").GetString()}")
                    };
                    portDirs[pname] = dir;
                }
            }

            foreach (var m in members.EnumerateArray())
            {
                string kind = m.GetProperty("kind").GetString()!;
                if (kind is "Variable" or "Net")
                {
                    string name = m.GetProperty("name").GetString()!;
                    string typeText = m.GetProperty("type").GetString()!;
                    if (TryParseUnpackedMemoryType(typeText, out int elemWidth, out int depth, out int lower, out bool memSigned, out bool memTwoState))
                    {
                        if (!memories.ContainsKey(name))
                        {
                            memories[name] = new MemDecl(name, elemWidth, depth, memSigned, lower, memTwoState);
                            memOrder.Add(name);
                        }
                    }
                    else
                    {
                        ParsePackedType(typeText, out int width, out bool isSigned, out bool isTwoState);
                        string dir = portDirs.GetValueOrDefault(name, "internal");
                        if (!signals.ContainsKey(name))
                        {
                            signals[name] = new SigDecl(name, width, dir, isSigned, isTwoState);
                            signalOrder.Add(name);
                        }
                    }
                }
                else if (kind == "ContinuousAssign")
                {
                    continuousNodes.Add(m);
                }
                else if (kind == "ProceduralBlock")
                {
                    processNodes.Add(m);
                }
            }
        }

        var ctx = new LowerContext(signals, memories);
        var continuousIr = new List<object>();
        foreach (var ca in continuousNodes)
        {
            var assign = ca.GetProperty("assignment");
            var lhs = ctx.LowerExpr(assign.GetProperty("left"));
            var rhs = ctx.LowerExpr(assign.GetProperty("right"));
            continuousIr.Add(new Dictionary<string, object?> { ["target"] = lhs, ["value"] = rhs });
        }

        var processesIr = new List<Dictionary<string, object?>>();
        var clocks = new List<string>();
        foreach (var pb in processNodes)
        {
            string pk = pb.GetProperty("procedureKind").GetString()!;
            var stmt = pb.GetProperty("body");
            if (pk is "AlwaysComb" or "AlwaysLatch")
            {
                ctx.CollectCombAssigns(stmt, continuousIr);
                continue;
            }
            if (pk is "AlwaysFF" or "Always")
            {
                if (stmt.GetProperty("kind").GetString() != "Timed")
                    throw new NotSupportedException($"Procedural block '{pk}' requires an event trigger (@(posedge/negedge ...)).");
                var events = new List<Dictionary<string, object?>>();
                ctx.CollectEvents(stmt.GetProperty("timing"), events, clocks);
                if (events.Count == 0)
                {
                    // Implicit always @(*) combinational block
                    ctx.CollectCombAssigns(stmt.GetProperty("stmt"), continuousIr);
                    continue;
                }
                var loweredBody = ctx.LowerStatement(stmt.GetProperty("stmt"));
                processesIr.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "process",
                    ["events"] = events,
                    ["body"] = loweredBody
                });
            }
        }

        string? primaryClock = clocks.FirstOrDefault(c => c.Equals("clk", StringComparison.OrdinalIgnoreCase) || c.Equals("clock", StringComparison.OrdinalIgnoreCase))
            ?? clocks.FirstOrDefault();

        var rootObj = new Dictionary<string, object?>
        {
            ["format"] = "velosim.rtl.v1",
            ["signals"] = signalOrder.Select(n =>
            {
                var s = signals[n];
                var d = new Dictionary<string, object?>
                {
                    ["name"] = s.Name,
                    ["width"] = s.Width,
                    ["direction"] = s.Direction
                };
                if (s.Signed) d["signed"] = true;
                if (s.TwoState) d["two_state"] = true;
                return d;
            }).ToArray(),
            ["memories"] = memOrder.Select(n =>
            {
                var m = memories[n];
                var d = new Dictionary<string, object?>
                {
                    ["name"] = m.Name,
                    ["width"] = m.Width,
                    ["depth"] = m.Depth
                };
                if (m.Signed) d["signed"] = true;
                if (m.Lower != 0) d["lower"] = m.Lower;
                if (m.TwoState) d["two_state"] = true;
                return d;
            }).ToArray(),
            ["continuous"] = continuousIr
        };

        if (processesIr.Count == 0)
        {
            rootObj["body"] = new Dictionary<string, object?> { ["kind"] = "block", ["items"] = Array.Empty<object>() };
        }
        else
        {
            rootObj["clock"] = primaryClock ?? throw new InvalidOperationException($"Module '{topName}' has sequential processes without an identifiable clock.");
            rootObj["body"] = processesIr.Count == 1
                ? processesIr[0]
                : new Dictionary<string, object?> { ["kind"] = "parallel", ["items"] = processesIr };
        }

        string irJson = SerializeIr(rootObj);
        // Fail closed immediately via VeloSim's canonical IR validator.
        _ = RtlDesign.Parse(irJson);
        return irJson;
    }

    private static string SerializeIr(Dictionary<string, object?> root)
    {
        using var ms = new MemoryStream(4096);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            WriteIrValue(w, root);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteIrValue(Utf8JsonWriter w, object? val)
    {
        switch (val)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case Dictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var kv in dict) { w.WritePropertyName(kv.Key); WriteIrValue(w, kv.Value); }
                w.WriteEndObject();
                break;
            case IEnumerable<object?> list:
                w.WriteStartArray();
                foreach (var item in list) WriteIrValue(w, item);
                w.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("Unsupported IR node type: " + val.GetType().Name);
        }
    }

    internal static string ResolveSlangExecutable(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;
        string? envPath = Environment.GetEnvironmentVariable("VELOSIM_SLANG");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;
        string localHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "slang", "slang");
        if (File.Exists(localHome))
            return localHome;
        return "slang";
    }

    private static JsonElement? FindTopInstance(JsonElement designEl, string? topModule)
    {
        if (!designEl.TryGetProperty("members", out var members)) return null;
        foreach (var m in members.EnumerateArray())
        {
            if (m.GetProperty("kind").GetString() != "Instance") continue;
            string name = m.GetProperty("name").GetString()!;
            if (topModule is null || name == topModule) return m;
        }
        return null;
    }

    [GeneratedRegex(@"^(logic|bit|reg|int|integer|byte|shortint|longint)(\s+signed|\s+unsigned)?(?:\[(\d+):(\d+)\])?$")]
    private static partial Regex PackedTypeRegex();

    private static bool TryParseUnpackedMemoryType(string typeText, out int width, out int depth, out int lower, out bool isSigned, out bool isTwoState)
    {
        width = depth = lower = 0; isSigned = isTwoState = false;
        int dollar = typeText.IndexOf('$');
        if (dollar < 0) return false;
        string elemPart = typeText[..dollar].Trim();
        string dimPart = typeText[(dollar + 1)..].Trim();
        var dm = Regex.Match(dimPart, @"^\[(\d+):(\d+)\]$");
        if (!dm.Success)
            throw new NotSupportedException($"Unsupported unpacked memory dimension syntax: {typeText}");
        int a = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(dm.Groups[2].Value, CultureInfo.InvariantCulture);
        lower = Math.Min(a, b);
        depth = Math.Abs(a - b) + 1;
        ParsePackedType(elemPart, out width, out isSigned, out isTwoState);
        return true;
    }

    private static void ParsePackedType(string typeText, out int width, out bool isSigned, out bool isTwoState)
    {
        string clean = typeText.Trim();
        var m = PackedTypeRegex().Match(clean);
        if (!m.Success)
            throw new NotSupportedException($"Unsupported SystemVerilog packed type: {typeText}");
        string baseTy = m.Groups[1].Value;
        string signSpec = m.Groups[2].Value.Trim();
        isTwoState = baseTy is "bit" or "int" or "byte" or "shortint" or "longint";
        isSigned = signSpec == "signed" || (signSpec.Length == 0 && baseTy is "int" or "integer" or "byte" or "shortint" or "longint");
        if (m.Groups[3].Success)
        {
            int hi = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int lo = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            width = Math.Abs(hi - lo) + 1;
        }
        else
        {
            width = baseTy switch
            {
                "byte" => 8,
                "shortint" => 16,
                "int" or "integer" => 32,
                "longint" => 64,
                _ => 1
            };
        }
    }

    private static string ParseSlangConstantToBits(string constant, int width)
    {
        string s = constant.Trim();
        if (s.StartsWith("'", StringComparison.Ordinal))
        {
            char fill = char.ToLowerInvariant(s.Length > 1 ? s[1] : '0');
            if (fill == '?') fill = 'z';
            return new string(fill, width);
        }
        int tick = s.IndexOf('\'');
        if (tick >= 0)
        {
            string rest = s[(tick + 1)..];
            int idx = 0;
            if (idx < rest.Length && (rest[idx] == 's' || rest[idx] == 'S')) idx++;
            char radix = idx < rest.Length ? char.ToLowerInvariant(rest[idx++]) : 'd';
            string digits = rest[idx..].Replace("_", "");
            return RadixDigitsToBits(digits, radix, width);
        }
        // Plain decimal integer (possibly negative)
        bool neg = s.StartsWith('-');
        string absText = neg ? s[1..] : s;
        ulong mag = ulong.Parse(absText, CultureInfo.InvariantCulture);
        ulong val = neg ? (0UL - mag) : mag;
        var chars = new char[width];
        for (int i = 0; i < width; ++i)
        {
            int bit = width - 1 - i;
            chars[i] = (bit < 64 && ((val >> bit) & 1UL) != 0) ? '1' : '0';
        }
        return new string(chars);
    }

    private static string RadixDigitsToBits(string digits, char radix, int width)
    {
        if (radix == 'd')
        {
            ulong val = ulong.Parse(digits, CultureInfo.InvariantCulture);
            var c = new char[width];
            for (int i = 0; i < width; ++i)
            {
                int bit = width - 1 - i;
                c[i] = (bit < 64 && ((val >> bit) & 1UL) != 0) ? '1' : '0';
            }
            return new string(c);
        }
        int group = radix switch { 'b' => 1, 'o' => 3, 'h' => 4, _ => throw new NotSupportedException($"Unsupported constant radix '{radix}'") };
        var sb = new StringBuilder(digits.Length * group);
        foreach (char ch in digits)
        {
            char lc = char.ToLowerInvariant(ch);
            if (lc is 'x' or 'z' or '?')
            {
                sb.Append(lc == '?' ? 'z' : lc, group);
            }
            else
            {
                int v = lc is >= '0' and <= '9' ? lc - '0' : (lc - 'a' + 10);
                for (int b = group - 1; b >= 0; --b)
                    sb.Append(((v >> b) & 1) != 0 ? '1' : '0');
            }
        }
        string raw = sb.ToString();
        if (raw.Length == width) return raw;
        if (raw.Length > width) return raw[^width..];
        char pad = raw.Length > 0 && raw[0] is 'x' or 'z' ? raw[0] : '0';
        return new string(pad, width - raw.Length) + raw;
    }

    private sealed class LowerContext(
        Dictionary<string, SigDecl> signals,
        Dictionary<string, MemDecl> memories)
    {
        private static string ExtractSymbolName(string symbolRef)
        {
            int sp = symbolRef.IndexOf(' ');
            return sp >= 0 ? symbolRef[(sp + 1)..] : symbolRef;
        }

        private static int TypeWidth(JsonElement e)
        {
            if (!e.TryGetProperty("type", out var tEl)) return 1;
            string t = tEl.GetString()!;
            if (t.Contains('$')) return 32;
            ParsePackedType(t, out int w, out _, out _);
            return w;
        }

        private static bool TypeSigned(JsonElement e)
        {
            if (!e.TryGetProperty("type", out var tEl)) return false;
            string t = tEl.GetString()!;
            if (t.Contains('$')) return false;
            ParsePackedType(t, out _, out bool s, out _);
            return s;
        }

        public void CollectEvents(JsonElement timing, List<Dictionary<string, object?>> events, List<string> clocks)
        {
            string kind = timing.GetProperty("kind").GetString()!;
            if (kind == "EventList")
            {
                foreach (var ev in timing.GetProperty("events").EnumerateArray())
                    CollectEvents(ev, events, clocks);
                return;
            }
            if (kind == "ImplicitEvent") return;
            if (kind == "SignalEvent")
            {
                string edge = timing.GetProperty("edge").GetString()! switch
                {
                    "PosEdge" => "posedge",
                    "NegEdge" => "negedge",
                    _ => throw new NotSupportedException($"Unsupported event edge: {timing.GetProperty("edge").GetString()}")
                };
                string sigName = ExtractSymbolName(timing.GetProperty("expr").GetProperty("symbol").GetString()!);
                events.Add(new Dictionary<string, object?> { ["signal"] = sigName, ["edge"] = edge });
                if (!clocks.Contains(sigName, StringComparer.Ordinal)) clocks.Add(sigName);
                return;
            }
            throw new NotSupportedException($"Unsupported timing control kind: {kind}");
        }

        public void CollectCombAssigns(JsonElement stmt, List<object> continuousIr)
        {
            string kind = stmt.GetProperty("kind").GetString()!;
            if (kind == "Block") { CollectCombAssigns(stmt.GetProperty("body"), continuousIr); return; }
            if (kind == "List") { foreach (var s in stmt.GetProperty("list").EnumerateArray()) CollectCombAssigns(s, continuousIr); return; }
            if (kind == "Empty") return;
            if (kind == "ExpressionStatement")
            {
                var expr = stmt.GetProperty("expr");
                if (expr.GetProperty("kind").GetString() == "Assignment")
                {
                    continuousIr.Add(new Dictionary<string, object?>
                    {
                        ["target"] = LowerExpr(expr.GetProperty("left")),
                        ["value"] = LowerExpr(expr.GetProperty("right"))
                    });
                    return;
                }
            }
            throw new NotSupportedException($"Unsupported combinational always statement kind: {kind}");
        }

        public Dictionary<string, object?> LowerStatement(JsonElement stmt)
        {
            string kind = stmt.GetProperty("kind").GetString()!;
            if (kind == "Empty")
                return new Dictionary<string, object?> { ["kind"] = "block", ["items"] = Array.Empty<object>() };
            if (kind == "Block")
                return LowerStatement(stmt.GetProperty("body"));
            if (kind == "List")
            {
                var items = stmt.GetProperty("list").EnumerateArray().Select(LowerStatement).ToArray();
                return new Dictionary<string, object?> { ["kind"] = "block", ["items"] = items };
            }
            if (kind == "ExpressionStatement")
            {
                var expr = stmt.GetProperty("expr");
                if (expr.GetProperty("kind").GetString() == "Assignment")
                {
                    bool isNba = expr.TryGetProperty("isNonBlocking", out var nb) && nb.GetBoolean();
                    return new Dictionary<string, object?>
                    {
                        ["kind"] = isNba ? "nba" : "blocking",
                        ["target"] = LowerExpr(expr.GetProperty("left")),
                        ["value"] = LowerExpr(expr.GetProperty("right"))
                    };
                }
                throw new NotSupportedException($"Unsupported expression statement: {expr.GetProperty("kind").GetString()}");
            }
            if (kind == "Conditional")
            {
                var conds = stmt.GetProperty("conditions").EnumerateArray().ToArray();
                if (conds.Length != 1) throw new NotSupportedException("Multi-condition pattern matching if is not supported.");
                var cond = LowerExpr(conds[0].GetProperty("expr"));
                var thenStmt = LowerStatement(stmt.GetProperty("ifTrue"));
                var elseStmt = stmt.TryGetProperty("ifFalse", out var ef) && ef.ValueKind == JsonValueKind.Object
                    ? LowerStatement(ef)
                    : new Dictionary<string, object?> { ["kind"] = "block", ["items"] = Array.Empty<object>() };
                return new Dictionary<string, object?>
                {
                    ["kind"] = "if",
                    ["condition"] = cond,
                    ["then"] = thenStmt,
                    ["else"] = elseStmt
                };
            }
            if (kind == "Case")
            {
                string condKind = stmt.TryGetProperty("condition", out var ck) ? ck.GetString()! : "Normal";
                string caseKind = condKind switch
                {
                    "WildcardZ" => "casez",
                    "WildcardXOrZ" => "casex",
                    _ => "exact"
                };
                var items = new List<Dictionary<string, object?>>();
                foreach (var it in stmt.GetProperty("items").EnumerateArray())
                {
                    var exprs = it.GetProperty("expressions").EnumerateArray().Select(LowerExpr).ToArray();
                    var body = LowerStatement(it.GetProperty("stmt"));
                    if (exprs.Length == 1)
                        items.Add(new Dictionary<string, object?> { ["match"] = exprs[0], ["body"] = body });
                    else
                        items.Add(new Dictionary<string, object?> { ["matches"] = exprs, ["body"] = body });
                }
                if (stmt.TryGetProperty("defaultCase", out var def) && def.ValueKind == JsonValueKind.Object)
                {
                    items.Add(new Dictionary<string, object?> { ["match"] = null, ["body"] = LowerStatement(def) });
                }
                return new Dictionary<string, object?>
                {
                    ["kind"] = "case",
                    ["case_kind"] = caseKind,
                    ["value"] = LowerExpr(stmt.GetProperty("expr")),
                    ["items"] = items
                };
            }
            throw new NotSupportedException($"Unsupported slang statement kind: {kind}");
        }

        public Dictionary<string, object?> LowerExpr(JsonElement e)
        {
            string kind = e.GetProperty("kind").GetString()!;

            // Use slang's pre-evaluated constant whenever available for literals / parameter refs / constant conversions.
            if (kind is "IntegerLiteral" or "UnbasedUnsizedIntegerLiteral" ||
                (kind == "NamedValue" && e.TryGetProperty("constant", out _)))
            {
                int w = TypeWidth(e);
                bool s = TypeSigned(e);
                string cText = e.TryGetProperty("constant", out var ce) ? ce.GetString()! : e.GetProperty("value").GetString()!;
                var res = new Dictionary<string, object?> { ["kind"] = "const", ["bits"] = ParseSlangConstantToBits(cText, w) };
                if (s) res["signed"] = true;
                return res;
            }

            if (kind == "Conversion")
            {
                if (e.TryGetProperty("constant", out var ce))
                {
                    int w = TypeWidth(e);
                    bool s = TypeSigned(e);
                    var res = new Dictionary<string, object?> { ["kind"] = "const", ["bits"] = ParseSlangConstantToBits(ce.GetString()!, w) };
                    if (s) res["signed"] = true;
                    return res;
                }
                var inner = LowerExpr(e.GetProperty("operand"));
                bool targetSigned = TypeSigned(e);
                bool srcSigned = TypeSigned(e.GetProperty("operand"));
                if (targetSigned != srcSigned)
                    return new Dictionary<string, object?> { ["kind"] = targetSigned ? "castsigned" : "castunsigned", ["args"] = new object[] { inner } };
                return inner;
            }

            if (kind == "NamedValue")
            {
                string name = ExtractSymbolName(e.GetProperty("symbol").GetString()!);
                if (!signals.ContainsKey(name))
                    throw new NotSupportedException($"Unsupported reference to non-signal symbol '{name}'.");
                return new Dictionary<string, object?> { ["kind"] = "ref", ["name"] = name };
            }

            if (kind == "ElementSelect")
            {
                var valEl = e.GetProperty("value");
                var selEl = e.GetProperty("selector");
                if (valEl.GetProperty("kind").GetString() == "NamedValue")
                {
                    string name = ExtractSymbolName(valEl.GetProperty("symbol").GetString()!);
                    if (memories.ContainsKey(name))
                    {
                        return new Dictionary<string, object?>
                        {
                            ["kind"] = "index",
                            ["name"] = name,
                            ["index"] = LowerExpr(selEl)
                        };
                    }
                }
                return new Dictionary<string, object?>
                {
                    ["kind"] = "bitselect",
                    ["value"] = LowerExpr(valEl),
                    ["index"] = LowerExpr(selEl)
                };
            }

            if (kind == "RangeSelect")
            {
                string sk = e.GetProperty("selectionKind").GetString()!;
                var valIr = LowerExpr(e.GetProperty("value"));
                if (sk == "Simple")
                {
                    int left = ParseIntConst(e.GetProperty("left"));
                    int right = ParseIntConst(e.GetProperty("right"));
                    return new Dictionary<string, object?>
                    {
                        ["kind"] = "partselect",
                        ["value"] = valIr,
                        ["left"] = left,
                        ["right"] = right
                    };
                }
                bool desc = sk == "IndexedDown";
                int width = ParseIntConst(e.GetProperty("right"));
                return new Dictionary<string, object?>
                {
                    ["kind"] = "indexedselect",
                    ["value"] = valIr,
                    ["base"] = LowerExpr(e.GetProperty("left")),
                    ["width"] = width,
                    ["descending"] = desc
                };
            }

            if (kind == "Concatenation")
            {
                var ops = e.GetProperty("operands").EnumerateArray().Select(LowerExpr).ToArray();
                var acc = ops[0];
                for (int i = 1; i < ops.Length; ++i)
                    acc = new Dictionary<string, object?> { ["kind"] = "concat", ["args"] = new object[] { acc, ops[i] } };
                return acc;
            }

            if (kind == "Replication")
            {
                int count = ParseIntConst(e.GetProperty("count"));
                var part = LowerExpr(e.GetProperty("concat"));
                var acc = part;
                for (int i = 1; i < count; ++i)
                    acc = new Dictionary<string, object?> { ["kind"] = "concat", ["args"] = new object[] { acc, part } };
                return acc;
            }

            if (kind == "ConditionalOp")
            {
                var conds = e.GetProperty("conditions").EnumerateArray().ToArray();
                return new Dictionary<string, object?>
                {
                    ["kind"] = "cond",
                    ["args"] = new object[] { LowerExpr(conds[0].GetProperty("expr")), LowerExpr(e.GetProperty("left")), LowerExpr(e.GetProperty("right")) }
                };
            }

            if (kind == "UnaryOp")
            {
                string op = e.GetProperty("op").GetString()! switch
                {
                    "Plus" => "pos",
                    "Minus" => "neg",
                    "BitwiseNot" => "bitnot",
                    "LogicalNot" => "not",
                    "BitwiseAnd" => "redand",
                    "BitwiseNand" => "rednand",
                    "BitwiseOr" => "redor",
                    "BitwiseNor" => "rednor",
                    "BitwiseXor" => "redxor",
                    "BitwiseXnor" => "redxnor",
                    var other => throw new NotSupportedException($"Unsupported slang unary op: {other}")
                };
                return new Dictionary<string, object?> { ["kind"] = op, ["args"] = new object[] { LowerExpr(e.GetProperty("operand")) } };
            }

            if (kind == "BinaryOp")
            {
                string op = e.GetProperty("op").GetString()! switch
                {
                    "Add" => "add",
                    "Subtract" => "sub",
                    "Multiply" => "mul",
                    "Divide" => "div",
                    "Mod" => "mod",
                    "BinaryAnd" => "bitand",
                    "BinaryOr" => "bitor",
                    "BinaryXor" => "bitxor",
                    "BinaryXnor" => "bitxnor",
                    "LogicalAnd" => "and",
                    "LogicalOr" => "or",
                    "LogicalShiftLeft" or "ArithmeticShiftLeft" => "shl",
                    "LogicalShiftRight" => "shr",
                    "ArithmeticShiftRight" => "ashr",
                    "Equality" => "eq",
                    "Inequality" => "neq",
                    "CaseEquality" => "caseeq",
                    "CaseInequality" => "caseneq",
                    "WildcardEquality" => "wildeq",
                    "WildcardInequality" => "wildneq",
                    "LessThan" => "lt",
                    "LessThanEqual" => "le",
                    "GreaterThan" => "gt",
                    "GreaterThanEqual" => "ge",
                    var other => throw new NotSupportedException($"Unsupported slang binary op: {other}")
                };
                return new Dictionary<string, object?>
                {
                    ["kind"] = op,
                    ["args"] = new object[] { LowerExpr(e.GetProperty("left")), LowerExpr(e.GetProperty("right")) }
                };
            }

            if (kind == "Call")
            {
                string fn = e.GetProperty("subroutine").GetString()!;
                var args = e.GetProperty("arguments").EnumerateArray().Select(LowerExpr).ToArray();
                string irKind = fn switch
                {
                    "$signed" => "castsigned",
                    "$unsigned" => "castunsigned",
                    "$countones" => "countones",
                    "$onehot" => "onehot",
                    "$onehot0" => "onehot0",
                    "$isunknown" => "isunknown",
                    "$clog2" => "clog2",
                    _ => throw new NotSupportedException($"Unsupported slang system call: {fn}")
                };
                return new Dictionary<string, object?> { ["kind"] = irKind, ["args"] = args.Cast<object>().ToArray() };
            }

            throw new NotSupportedException($"Unsupported slang expression kind: {kind}");
        }

        private static int ParseIntConst(JsonElement e)
        {
            string c = e.TryGetProperty("constant", out var ce) ? ce.GetString()! : e.GetProperty("value").GetString()!;
            int tick = c.IndexOf('\'');
            if (tick >= 0)
            {
                string bits = ParseSlangConstantToBits(c, 32);
                return Convert.ToInt32(bits, 2);
            }
            return int.Parse(c, CultureInfo.InvariantCulture);
        }
    }
}
