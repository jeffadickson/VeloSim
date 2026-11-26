using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlLinterFlagshipTests
{
    [Test]
    public void DetectsShiftOvfDivZeroAssignInInfLoopCaseWithXAndIfDepth()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "in_a", "width": 8, "direction": "input"},
                {"name": "sel", "width": 2, "direction": "input"},
                {"name": "sh_out", "width": 8, "direction": "output"},
                {"name": "div_out", "width": 8, "direction": "output"},
                {"name": "case_out", "width": 4, "direction": "output"},
                {"name": "deep_out", "width": 4, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "in_a"}, "value": {"kind": "const", "bits": "00000001"}},
                {"target": {"kind": "ref", "name": "sh_out"}, "value": {"kind": "shl", "args": [{"kind": "ref", "name": "in_a"}, {"kind": "const", "bits": "1000"}]}},
                {"target": {"kind": "ref", "name": "div_out"}, "value": {"kind": "div", "args": [{"kind": "ref", "name": "in_a"}, {"kind": "const", "bits": "00000000"}]}}
              ],
              "combinational": [
                {
                  "kind": "block",
                  "items": [
                    {
                      "kind": "case",
                      "case_kind": "casez",
                      "value": {"kind": "ref", "name": "sel"},
                      "items": [
                        {"match": {"kind": "const", "bits": "1x"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "case_out"}, "value": {"kind": "const", "bits": "0001"}}},
                        {"match": null, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "case_out"}, "value": {"kind": "const", "bits": "0000"}}}
                      ]
                    },
                    {
                      "kind": "if",
                      "condition": {"kind": "eq", "args": [{"kind": "ref", "name": "sel"}, {"kind": "const", "bits": "00"}]},
                      "then": {"kind": "blocking", "target": {"kind": "ref", "name": "deep_out"}, "value": {"kind": "const", "bits": "0001"}},
                      "else": {
                        "kind": "if",
                        "condition": {"kind": "eq", "args": [{"kind": "ref", "name": "sel"}, {"kind": "const", "bits": "01"}]},
                        "then": {"kind": "blocking", "target": {"kind": "ref", "name": "deep_out"}, "value": {"kind": "const", "bits": "0010"}},
                        "else": {
                          "kind": "if",
                          "condition": {"kind": "eq", "args": [{"kind": "ref", "name": "sel"}, {"kind": "const", "bits": "10"}]},
                          "then": {"kind": "blocking", "target": {"kind": "ref", "name": "deep_out"}, "value": {"kind": "const", "bits": "0100"}},
                          "else": {"kind": "blocking", "target": {"kind": "ref", "name": "deep_out"}, "value": {"kind": "const", "bits": "1000"}}
                        }
                      }
                    },
                    {
                      "kind": "while",
                      "condition": {"kind": "const", "bits": "1"},
                      "body": {"kind": "block", "items": []}
                    }
                  ]
                }
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        var opts = new LintOptions(Wall: true, Parameters: new LintRuleParameters(MaxIfDepth: 2));
        var report = RtlLinter.LintJson(ir, opts);
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();

        Assert.That(codes, Contains.Item("ASSIGNIN"), report.FormatText());
        Assert.That(codes, Contains.Item("SHIFTOVF"), report.FormatText());
        Assert.That(codes, Contains.Item("DIVZERO"), report.FormatText());
        Assert.That(codes, Contains.Item("CASEWITHX"), report.FormatText());
        Assert.That(codes, Contains.Item("IFDEPTH"), report.FormatText());
        Assert.That(codes, Contains.Item("INFLOOP"), report.FormatText());
    }

    [Test]
    public void DetectsSyncAsyncNetAndResetConsistencyWithoutFalsePositivesOnCleanAsyncReset()
    {
        // Clean async reset: rst is on @(posedge clk or posedge rst) and resets BOTH q1 and q2 -> 0 SYNCASYNCNET, 0 RESETCONSIST
        string cleanAsyncIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "d", "width": 8, "direction": "input"},
                {"name": "q1", "width": 8, "direction": "output"},
                {"name": "q2", "width": 8, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "clock": "clk",
              "body": {
                "kind": "process",
                "events": [{"signal": "clk", "edge": "posedge"}, {"signal": "rst", "edge": "posedge"}],
                "body": {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "rst"},
                  "then": {
                    "kind": "block",
                    "items": [
                      {"kind": "nba", "target": {"kind": "ref", "name": "q1"}, "value": {"kind": "const", "bits": "00000000"}},
                      {"kind": "nba", "target": {"kind": "ref", "name": "q2"}, "value": {"kind": "const", "bits": "00000000"}}
                    ]
                  },
                  "else": {
                    "kind": "block",
                    "items": [
                      {"kind": "nba", "target": {"kind": "ref", "name": "q1"}, "value": {"kind": "ref", "name": "d"}},
                      {"kind": "nba", "target": {"kind": "ref", "name": "q2"}, "value": {"kind": "ref", "name": "q1"}}
                    ]
                  }
                }
              }
            }
            """;

        var cleanReport = RtlLinter.LintJson(cleanAsyncIr, new LintOptions(Profile: LintProfile.Signoff));
        Assert.That(cleanReport.Passed, Is.True, cleanReport.FormatText());
        Assert.That(cleanReport.Diagnostics, Is.Empty, cleanReport.FormatText());

        // Buggy: rst used as async trigger in process[0], ALSO used synchronously in process[1] (without rst in events),
        // AND process[0] forgets to reset q2!
        string buggyIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "d", "width": 8, "direction": "input"},
                {"name": "q1", "width": 8, "direction": "output"},
                {"name": "q2", "width": 8, "direction": "output"},
                {"name": "q3", "width": 8, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "clock": "clk",
              "body": {
                "kind": "parallel",
                "items": [
                  {
                    "kind": "process",
                    "events": [{"signal": "clk", "edge": "posedge"}, {"signal": "rst", "edge": "posedge"}],
                    "body": {
                      "kind": "if",
                      "condition": {"kind": "ref", "name": "rst"},
                      "then": {"kind": "nba", "target": {"kind": "ref", "name": "q1"}, "value": {"kind": "const", "bits": "00000000"}},
                      "else": {
                        "kind": "block",
                        "items": [
                          {"kind": "nba", "target": {"kind": "ref", "name": "q1"}, "value": {"kind": "ref", "name": "d"}},
                          {"kind": "nba", "target": {"kind": "ref", "name": "q2"}, "value": {"kind": "ref", "name": "q1"}}
                        ]
                      }
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk", "edge": "posedge"}],
                    "body": {
                      "kind": "if",
                      "condition": {"kind": "ref", "name": "rst"},
                      "then": {"kind": "nba", "target": {"kind": "ref", "name": "q3"}, "value": {"kind": "const", "bits": "00000000"}},
                      "else": {"kind": "nba", "target": {"kind": "ref", "name": "q3"}, "value": {"kind": "ref", "name": "d"}}
                    }
                  }
                ]
              }
            }
            """;

        var buggyReport = RtlLinter.LintJson(buggyIr, new LintOptions(Profile: LintProfile.Signoff));
        Assert.That(buggyReport.Passed, Is.False, buggyReport.FormatText());
        Assert.That(buggyReport.Diagnostics.Any(d => d.Code == "SYNCASYNCNET" && d.Severity == LintSeverity.Error && d.Location?.Signal == "rst"), Is.True, buggyReport.FormatText());
        Assert.That(buggyReport.Diagnostics.Any(d => d.Code == "RESETCONSIST" && d.Severity == LintSeverity.Error && d.Location?.Signal == "q2"), Is.True, buggyReport.FormatText());
    }

    [Test]
    public void NamedProfilesPromoteAppropriateRuleSets()
    {
        string fourStateIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "a", "width": 8, "direction": "input"},
                {"name": "y", "width": 1, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "y"}, "value": {"kind": "eq", "args": [{"kind": "ref", "name": "a"}, {"kind": "const", "bits": "xxxxxxxx"}]}}
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        var wall = RtlLinter.LintJson(fourStateIr, new LintOptions(Profile: LintProfile.Wall));
        Assert.That(wall.Passed, Is.True);
        Assert.That(wall.Diagnostics[0].Severity, Is.EqualTo(LintSeverity.Warning));

        var fourState = RtlLinter.LintJson(fourStateIr, new LintOptions(Profile: LintProfile.FourState));
        Assert.That(fourState.Passed, Is.False);
        Assert.That(fourState.Profile, Is.EqualTo("fourstate"));
        Assert.That(fourState.Diagnostics[0].Severity, Is.EqualTo(LintSeverity.Error));

        Assert.That(LintProfiles.Parse("synthesis"), Is.EqualTo(LintProfile.Signoff));
        Assert.That(LintProfiles.Parse("4state"), Is.EqualTo(LintProfile.FourState));
        Assert.That(() => LintProfiles.Parse("nonexistent"), Throws.ArgumentException);
    }

    [Test]
    public void Tier1ConfigSupportsRuleSeveritiesParametersNamingBudgetsAndScopedWaivers()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "BadClk", "width": 1, "direction": "input"},
                {"name": "dbg_tap", "width": 8, "direction": "input"},
                {"name": "legacy_unused", "width": 8, "direction": "input"},
                {"name": "wide_bus", "width": 64, "direction": "input"},
                {"name": "q_out", "width": 64, "direction": "output"}
              ],
              "memories": [
                {"name": "BIGMEM", "width": 8, "depth": 128}
              ],
              "continuous": [],
              "clock": "BadClk",
              "body": {
                "kind": "nba",
                "target": {"kind": "ref", "name": "q_out"},
                "value": {"kind": "add", "args": [{"kind": "ref", "name": "wide_bus"}, {"kind": "const", "bits": "0001"}]}
              }
            }
            """;

        string cfgJson = """
            {
              "schema": "velosim.lint.config.v1",
              "profile": "wall",
              "warn_unused_waivers": true,
              "rules": {
                "UNUSEDMEM": "off",
                "WIDTHBUDGET": "error"
              },
              "params": {
                "unused_ignore_patterns": ["dbg_*"],
                "allow_literal_width_expand": true,
                "max_signal_width": 32,
                "max_memory_depth": 64,
                "naming": {
                  "clock_pattern": "^clk",
                  "memory_pattern": "^[a-z][a-z0-9_]*$"
                }
              },
              "waivers": [
                {
                  "id": "W-LEGACY",
                  "code": "UNUSEDSIGNAL",
                  "file": "rtl/**",
                  "signal": "legacy_*",
                  "reason": "Reserved pin for rev B",
                  "owner": "soc",
                  "expires": "2028-01-01"
                },
                {
                  "id": "W-STALE",
                  "code": "LATCH",
                  "signal": "no_such_signal",
                  "reason": "Old waiver that should trigger UNUSEDWAIVER and EXPIREDWAIVER",
                  "expires": "2025-01-01"
                }
              ]
            }
            """;

        var config = LintConfig.Parse(cfgJson);
        var opts = new LintOptions(Config: config, AsOfDate: new DateOnly(2026, 11, 26));
        var report = RtlLinter.LintJson(ir, opts, "rtl/core/sub.rtl.json");

        // dbg_tap ignored by unused_ignore_patterns; legacy_unused waived by W-LEGACY -> 0 UNUSEDSIGNAL!
        Assert.That(report.Diagnostics.Any(d => d.Code == "UNUSEDSIGNAL"), Is.False, report.FormatText());
        Assert.That(report.WaivedCount, Is.EqualTo(1));

        // allow_literal_width_expand suppresses WIDTHEXPAND on 4-bit const added to 64-bit wide_bus
        Assert.That(report.Diagnostics.Any(d => d.Code == "WIDTHEXPAND"), Is.False, report.FormatText());

        // UNUSEDMEM turned off by config
        Assert.That(report.Diagnostics.Any(d => d.Code == "UNUSEDMEM"), Is.False, report.FormatText());

        // WIDTHBUDGET promoted to error by config (wide_bus > 32, q_out > 32, BIGMEM depth 128 > 64)
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Diagnostics.Count(d => d.Code == "WIDTHBUDGET" && d.Severity == LintSeverity.Error), Is.EqualTo(3), report.FormatText());

        // NAMING catches BadClk and BIGMEM
        Assert.That(report.Diagnostics.Any(d => d.Code == "NAMING" && d.Location?.Signal == "BadClk"), Is.True, report.FormatText());
        Assert.That(report.Diagnostics.Any(d => d.Code == "NAMING" && d.Location?.Signal == "BIGMEM"), Is.True, report.FormatText());

        // W-STALE triggers both UNUSEDWAIVER and EXPIREDWAIVER
        Assert.That(report.Diagnostics.Any(d => d.Code == "UNUSEDWAIVER" && d.Message.Contains("W-STALE")), Is.True, report.FormatText());
        Assert.That(report.Diagnostics.Any(d => d.Code == "EXPIREDWAIVER" && d.Message.Contains("W-STALE")), Is.True, report.FormatText());

        // CLI -Wno-WIDTHBUDGET overrides config "error"
        var cliOverride = RtlLinter.LintJson(ir, opts with { DisabledRules = new HashSet<string> { "WIDTHBUDGET" } }, "rtl/core/sub.rtl.json");
        Assert.That(cliOverride.Diagnostics.Any(d => d.Code == "WIDTHBUDGET"), Is.False);
    }

    [Test]
    public void Tier1ConfigValidationRejectsInvalidSchemasRegexesAndDates()
    {
        Assert.That(() => LintConfig.Parse("{\"schema\":\"bad.v1\"}"), Throws.ArgumentException);
        Assert.That(() => LintConfig.Parse("{\"rules\":{\"WIDTHTRUNC\":\"bogus\"}}"), Throws.ArgumentException);
        Assert.That(() => LintConfig.Parse("{\"params\":{\"max_if_depth\":0}}"), Throws.ArgumentException);
        Assert.That(() => LintConfig.Parse("{\"params\":{\"naming\":{\"signal_pattern\":\"[unclosed\"}}}"), Throws.ArgumentException);
        Assert.That(() => LintConfig.Parse("{\"waivers\":[{\"code\":\"LATCH\",\"expires\":\"not-a-date\"}]}"), Throws.ArgumentException);
        Assert.That(() => LintConfig.Parse("{\"waivers\":[{\"code\":\"\"}]}"), Throws.ArgumentException);
        Assert.That(() => LintBaseline.Parse("{\"schema\":\"wrong\"}"), Throws.ArgumentException);
    }

    [Test]
    public void Tier2CustomIRtlLintRuleParticipatesInWaiversConfigAndSarif()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "a", "width": 32, "direction": "input"},
                {"name": "b", "width": 32, "direction": "input"},
                {"name": "q", "width": 32, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "q"}, "value": {"kind": "div", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}}
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        // Register a Tier 2 custom rule (same pattern Tier 3 Cameo DSL rules will compile to)
        var customInfo = new LintRuleInfo(
            "NO_WIDE_DIV", true, LintSeverity.Warning, "AREA",
            "Disallow runtime division wider than 16 bits for area budget.",
            "Replace wide division with shift/multiply or multi-cycle divider.");
        var registry = new RtlLintRuleRegistry()
            .Add(new DelegateRtlLintRule(customInfo, ctx =>
            {
                Assert.That(ctx.Clock, Is.Null);
                Assert.That(ctx.IsTimed, Is.False);
                Assert.That(ctx.Root.ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(ctx.Parameters.MaxIfDepth, Is.EqualTo(8));
                Assert.That(ctx.Signals.Any(s => s.Name == "a" && s.Width == 32 && s.Direction == "input" && !s.Signed && !s.TwoState && s.SourcePath is null && s.IsRead && !s.IsWritten && !s.IsClockOrEvent), Is.True);
                Assert.That(ctx.Memories, Is.Empty);
                Assert.That(ctx.Assignments.Any(a => a.TargetOwner == "q" && a.TargetDescription == "q" && a.TargetWidth == 32 && a.ValueWidth == 32 && a.TargetSigned == false && a.ValueSigned == false && a.IsBlocking && !a.InClockedProcess && a.Context == "continuous[0]" && a.TargetNode.ValueKind == JsonValueKind.Object && a.ValueNode.ValueKind == JsonValueKind.Object), Is.True);
                Assert.That(ctx.Statements.Any(st => st.Kind == "block" && st.Context == "body" && st.Depth == 0 && !st.InClockedProcess && st.Node.ValueKind == JsonValueKind.Object), Is.True);
                foreach (var expr in ctx.Expressions)
                {
                    if (expr.Kind == "div" && expr.Width is > 16 && expr.Signed == false && expr.Node.ValueKind == JsonValueKind.Object)
                        ctx.Report(customInfo, $"Runtime {expr.Width}-bit division exceeds 16-bit area budget.", signal: "q", context: expr.Context);
                }
            }));

        // Also test LintWaiver Context and Message glob matching
        var ctxWaiver = new LintWaiver("W-CTX", "NO_WIDE_DIV", Context: "continuous*", Message: "*area budget*");
        Assert.That(ctxWaiver.Matches(new LintDiagnostic("NO_WIDE_DIV", LintSeverity.Warning, "Runtime 32-bit division exceeds 16-bit area budget.", "custom", new LintLocation("rtl/alu.rtl.json", null, null, "q", "continuous[0]"))), Is.True);
        Assert.That(ctxWaiver.Matches(new LintDiagnostic("NO_WIDE_DIV", LintSeverity.Warning, "Other message", "custom", new LintLocation("rtl/alu.rtl.json", null, null, "q", "continuous[0]"))), Is.False);
        Assert.That(ctxWaiver.Matches(new LintDiagnostic("NO_WIDE_DIV", LintSeverity.Warning, "Runtime 32-bit division exceeds 16-bit area budget.", "custom", new LintLocation("rtl/alu.rtl.json", null, null, "q", "body"))), Is.False);

        // Duplicate code throws
        Assert.That(() => registry.Add("NO_WIDE_DIV", "dup", _ => { }), Throws.ArgumentException);

        // Config promotes custom rule NO_WIDE_DIV to error!
        var config = LintConfig.Parse("{\"rules\":{\"NO_WIDE_DIV\":\"error\"}}");
        var report = RtlLinter.LintJson(ir, new LintOptions(Wall: true, Config: config, CustomRules: registry.Rules), "rtl/alu.rtl.json");
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Diagnostics.Count, Is.EqualTo(1));
        Assert.That(report.Diagnostics[0].Code, Is.EqualTo("NO_WIDE_DIV"));
        Assert.That(report.Diagnostics[0].Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(report.Diagnostics[0].Source, Is.EqualTo("custom"));

        // Verify SARIF 2.1.0 output includes custom rule metadata, hint, location, and fingerprint
        string sarifJson = report.ToSarif(registry.Rules);
        using var sarif = JsonDocument.Parse(sarifJson);
        Assert.That(sarif.RootElement.GetProperty("$schema").GetString(), Does.Contain("sarif-2.1.0"));
        Assert.That(sarif.RootElement.GetProperty("version").GetString(), Is.EqualTo("2.1.0"));
        var run = sarif.RootElement.GetProperty("runs")[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.That(driver.GetProperty("name").GetString(), Is.EqualTo("VeloSim"));
        Assert.That(driver.GetProperty("rules")[0].GetProperty("id").GetString(), Is.EqualTo("NO_WIDE_DIV"));
        Assert.That(driver.GetProperty("rules")[0].GetProperty("help").GetProperty("text").GetString(), Does.Contain("multi-cycle divider"));
        var res = run.GetProperty("results")[0];
        Assert.That(res.GetProperty("ruleId").GetString(), Is.EqualTo("NO_WIDE_DIV"));
        Assert.That(res.GetProperty("level").GetString(), Is.EqualTo("error"));
        Assert.That(res.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString(), Is.EqualTo("rtl/alu.rtl.json"));
        Assert.That(res.GetProperty("locations")[0].GetProperty("logicalLocations")[0].GetProperty("name").GetString(), Is.EqualTo("q"));
        Assert.That(res.GetProperty("partialFingerprints").GetProperty("velosim/v1").GetString(), Has.Length.EqualTo(16));
    }

    [Test]
    public void SlangLinterForwardsConfigRulesAndWaiverFile()
    {
        var cfg = LintConfig.Parse("""
            {
              "profile": "signoff",
              "warn_unused_waivers": true,
              "slang_waiver_file": "waivers.toml",
              "rules": {
                "WIDTHEXPAND": "off",
                "LATCH": "error"
              }
            }
            """);
        var args = SlangLinter.BuildSlangArguments(["top.sv"], new LintOptions(Config: cfg), top: "top");
        Assert.That(args, Contains.Item("-Weverything"));
        Assert.That(args, Contains.Item("-Wno-width-expand"));
        Assert.That(args, Contains.Item("-Werror=inferred-latch"));
        Assert.That(args, Contains.Item("--waiver-file"));
        Assert.That(args, Contains.Item("--warn-unused-waivers"));
    }

    [Test]
    public async Task CliSupportsListRulesInitConfigAutoDiscoverBaselineRatchetAndSarif()
    {
        // --list-rules text & JSON
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["lint", "--list-rules"], stdout, stderr), Is.Zero);
        Assert.That(stdout.ToString(), Does.Contain("WIDTHTRUNC"));
        Assert.That(stdout.ToString(), Does.Contain("FOURSTATE"));
        Assert.That(stdout.ToString(), Does.Contain("Hint:"));

        stdout = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["lint", "--list-rules", "--json"], stdout, stderr), Is.Zero);
        using (var doc = JsonDocument.Parse(stdout.ToString()))
        {
            Assert.That(doc.RootElement.GetProperty("schema").GetString(), Is.EqualTo("velosim.lint.rules.v1"));
            Assert.That(doc.RootElement.GetProperty("rules").GetArrayLength(), Is.GreaterThanOrEqualTo(30));
        }

        string dir = Path.Combine(Path.GetTempPath(), "velosim-flagship-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // --init-config creates a valid parseable velosim-lint.json
            string cfgPath = Path.Combine(dir, "velosim-lint.json");
            stdout = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["lint", "--init-config", cfgPath], stdout, stderr), Is.Zero);
            Assert.That(File.Exists(cfgPath), Is.True);
            var loadedCfg = LintConfig.LoadFile(cfgPath);
            Assert.That(loadedCfg.Profile, Is.EqualTo(LintProfile.Wall));

            // Write a legacy IR file with a WIDTHTRUNC warning
            string legacyIrPath = Path.Combine(dir, "legacy.rtl.json");
            await File.WriteAllTextAsync(legacyIrPath, """
                {
                  "format": "velosim.rtl.v1",
                  "signals": [
                    {"name": "a", "width": 16, "direction": "input"},
                    {"name": "y", "width": 8, "direction": "output"}
                  ],
                  "memories": [],
                  "continuous": [
                    {"target": {"kind": "ref", "name": "y"}, "value": {"kind": "ref", "name": "a"}}
                  ],
                  "clock": null,
                  "body": {"kind": "block", "items": []}
                }
                """);

            // Write baseline (using --no-config so the example waiver doesn't trigger UNUSEDWAIVER)
            string basePath = Path.Combine(dir, "baseline.json");
            stdout = new StringWriter();
            int rcWrite = await ToolCli.RunAsync(["lint", legacyIrPath, "--no-config", "-Wall", "--write-baseline", basePath], stdout, stderr);
            Assert.That(rcWrite, Is.Zero, stderr.ToString());
            Assert.That(File.Exists(basePath), Is.True);

            // Re-run with --baseline and -Wall -Werror -> passes because legacy warning is baselined!
            stdout = new StringWriter();
            int rcRatchet = await ToolCli.RunAsync(["lint", legacyIrPath, "--no-config", "-Wall", "-Werror", "--baseline", basePath, "--json"], stdout, stderr);
            Assert.That(rcRatchet, Is.Zero, stdout.ToString() + stderr.ToString());
            using (var doc = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("pass"));
                Assert.That(doc.RootElement.GetProperty("baselined_count").GetInt32(), Is.EqualTo(1));
                Assert.That(doc.RootElement.GetProperty("diagnostics").GetArrayLength(), Is.Zero);
            }

            // Emit SARIF 2.1.0 with --profile signoff (WIDTHTRUNC becomes error)
            string sarifPath = Path.Combine(dir, "out.sarif");
            stdout = new StringWriter();
            int rcSarif = await ToolCli.RunAsync(["lint", legacyIrPath, "--no-config", "--profile", "signoff", "--sarif", "--output", sarifPath], stdout, stderr);
            Assert.That(rcSarif, Is.EqualTo(1));
            using (var sdoc = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath)))
            {
                Assert.That(sdoc.RootElement.GetProperty("version").GetString(), Is.EqualTo("2.1.0"));
                Assert.That(sdoc.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("level").GetString(), Is.EqualTo("error"));
            }

            // Auto-discovery picks up velosim-lint.json in same directory (whose example waiver is unused -> UNUSEDWAIVER)
            stdout = new StringWriter();
            int rcAuto = await ToolCli.RunAsync(["lint", legacyIrPath], stdout, stderr);
            Assert.That(rcAuto, Is.Zero);
            Assert.That(stdout.ToString(), Does.Contain("UNUSEDWAIVER"));

            // Combining --json and --sarif is rejected
            stdout = new StringWriter();
            stderr = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["lint", legacyIrPath, "--json", "--sarif"], stdout, stderr), Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("Cannot combine --json and --sarif"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public void GlobMatcherHandlesWildcardsAndDoubleStarPaths()
    {
        Assert.That(LintGlob.IsMatch("dbg_*", "dbg_tap"), Is.True);
        Assert.That(LintGlob.IsMatch("dbg_*", "clk_dbg"), Is.False);
        Assert.That(LintGlob.IsMatch("q?", "q1"), Is.True);
        Assert.That(LintGlob.IsMatch("q?", "q12"), Is.False);
        Assert.That(LintGlob.IsMatch("vendor/**", "vendor/ip/core/alu.sv"), Is.True);
        Assert.That(LintGlob.IsMatch("vendor/*.sv", "vendor/sub/alu.sv"), Is.False);
        Assert.That(LintGlob.IsMatch("vendor/*.sv", "vendor/alu.sv"), Is.True);
    }
}
