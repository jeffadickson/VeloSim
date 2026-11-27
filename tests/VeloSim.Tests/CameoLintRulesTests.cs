using System.IO.Compression;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class CameoLintRulesTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "VeloSim.slnx")))
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("Could not locate VeloSim.slnx");
        return dir;
    }

    private static string SocPoliciesPath() =>
        Path.Combine(RepoRoot(), "examples", "lint_rules", "soc_policies.cameo");

    [Test]
    public void ExampleSocPoliciesPackTypeChecksAndDetectsMemDivFsmAndCdcViolations()
    {
        string packPath = SocPoliciesPath();
        var checkErrors = CameoLintRulePack.CheckFile(packPath);
        Assert.That(checkErrors, Is.Empty, string.Join("\n", checkErrors));

        var pack = CameoLintRulePack.LoadFile(packPath);
        Assert.That(pack.Rules.Count, Is.EqualTo(4));
        Assert.That(pack.Rules.Select(r => r.Info.Code), Is.EquivalentTo(new[]
        {
            "SOC_POW2_MEM", "SOC_NO_WIDE_DIV", "SOC_FSM_MIN_WIDTH", "SOC_CDC_NO_BUS"
        }));

        // Design violating all 4 rules in soc_policies.cameo:
        // 1. Memory 'bad_ram' depth 300 (not power of 2) -> SOC_POW2_MEM (phase: rtl, error)
        // 2. 32-bit div expression -> SOC_NO_WIDE_DIV (phase: rtl, warning)
        // 3. 1-process FSM without reset or default recovery -> SOC_FSM_MIN_WIDTH (phase: fsm, warning)
        // 4. 8-bit bus crossing clk_a -> clk_b -> SOC_CDC_NO_BUS (phase: cdc, error)
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk_a", "width": 1, "direction": "input"},
                {"name": "clk_b", "width": 1, "direction": "input"},
                {"name": "num", "width": 32, "direction": "input"},
                {"name": "den", "width": 32, "direction": "input"},
                {"name": "bus_in", "width": 8, "direction": "input"},
                {"name": "quot", "width": 32, "direction": "output"},
                {"name": "state", "width": 2, "direction": "output"},
                {"name": "bus_a", "width": 8, "direction": "internal"},
                {"name": "bus_b", "width": 8, "direction": "output"},
                {"name": "ram_out", "width": 8, "direction": "output"}
              ],
              "memories": [
                {"name": "bad_ram", "width": 8, "depth": 300}
              ],
              "continuous": [
                {"target": {"kind": "ref", "name": "quot"}, "value": {"kind": "div", "args": [{"kind": "ref", "name": "num"}, {"kind": "ref", "name": "den"}]}},
                {"target": {"kind": "ref", "name": "ram_out"}, "value": {"kind": "memread", "memory": "bad_ram", "index": {"kind": "const", "bits": "000000000"}}}
              ],
              "clock": "clk_a",
              "body": {
                "kind": "parallel",
                "items": [
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_a", "edge": "posedge"}],
                    "body": {
                      "kind": "block",
                      "items": [
                        {"kind": "nba", "target": {"kind": "ref", "name": "bus_a"}, "value": {"kind": "ref", "name": "bus_in"}},
                        {
                          "kind": "case",
                          "value": {"kind": "ref", "name": "state"},
                          "items": [
                            {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                            {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}}
                          ]
                        }
                      ]
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_b", "edge": "posedge"}],
                    "body": {"kind": "nba", "target": {"kind": "ref", "name": "bus_b"}, "value": {"kind": "ref", "name": "bus_a"}}
                  }
                ]
              }
            }
            """;

        var report = RtlLinter.LintJson(ir, new LintOptions(Wall: true, CameoRuleScripts: [packPath]), "soc.sv");
        var byCode = report.Diagnostics.ToLookup(d => d.Code);

        Assert.That(byCode.Contains("SOC_POW2_MEM"), Is.True, report.FormatText());
        Assert.That(byCode.Contains("SOC_NO_WIDE_DIV"), Is.True, report.FormatText());
        Assert.That(byCode.Contains("SOC_FSM_MIN_WIDTH"), Is.True, report.FormatText());
        Assert.That(byCode.Contains("SOC_CDC_NO_BUS"), Is.True, report.FormatText());

        var memDiag = byCode["SOC_POW2_MEM"].First();
        Assert.That(memDiag.Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(memDiag.Phase, Is.EqualTo("rtl"));
        Assert.That(memDiag.Source, Is.EqualTo("cameo"));
        Assert.That(memDiag.GuidelineTags, Contains.Item("CORP-MEM-001"));
        Assert.That(memDiag.GuidelineTags, Contains.Item("DO-254"));

        var cdcDiag = byCode["SOC_CDC_NO_BUS"].First();
        Assert.That(cdcDiag.Phase, Is.EqualTo("cdc"));
        Assert.That(cdcDiag.GuidelineTags, Contains.Item("CORP-CDC-003"));

        // SARIF 2.1.0 includes the Cameo rule metadata, tags, and phase
        using var sarif = JsonDocument.Parse(report.ToSarif());
        var sarifRules = sarif.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var sarifMem = sarifRules.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "SOC_POW2_MEM");
        Assert.That(sarifMem.GetProperty("properties").GetProperty("phase").GetString(), Is.EqualTo("rtl"));
        Assert.That(sarifMem.GetProperty("properties").GetProperty("tags").EnumerateArray().Select(e => e.GetString()), Contains.Item("CORP-MEM-001"));

        // Phase filtering works for Cameo rules too
        var cdcPhaseOnly = RtlLinter.LintJson(ir, new LintOptions(Wall: true, Phases: LintPhases.ParseList("cdc"), CameoRuleScripts: [packPath]));
        Assert.That(cdcPhaseOnly.Diagnostics.Any(d => d.Code == "SOC_CDC_NO_BUS"), Is.True);
        Assert.That(cdcPhaseOnly.Diagnostics.Any(d => d.Code == "SOC_POW2_MEM"), Is.False);
    }

    [Test]
    public void Rv32iCorePassesSocPoliciesCameoPackWithZeroFindings()
    {
        string gzPath = Path.Combine(RepoRoot(), "tests", "VeloSim.Tests", "Fixtures", "rv32i_core.slang.ast.json.gz");
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        string irJson = SlangAstLowerer.LowerAstJson(sr.ReadToEnd(), "rv32i_core");

        var report = RtlLinter.LintJson(irJson, new LintOptions(Profile: LintProfile.Do254, CameoRuleScripts: [SocPoliciesPath()]), "examples/rv32i_core.sv");
        Assert.That(report.ErrorCount, Is.EqualTo(0), report.FormatText());
        Assert.That(report.WarningCount, Is.EqualTo(0), report.FormatText());
        Assert.That(report.EffectiveQuality.Score, Is.EqualTo(100.0));
    }

    [Test]
    public void StaticTypeCheckerCatchesFieldTyposTypeMismatchesAndSyntaxErrors()
    {
        string badFieldScript = """
            rule({
                code = "BAD_FIELD",
                summary = "Typo in signal field."
            }, (ctx: LintContext) => {
                for s in ctx.signals {
                    if s.nonexistent_field > 8 {
                        ctx.report("BAD_FIELD", "warning", s.name)
                    }
                }
            })
            """;

        var fieldErrors = CameoLintRulePack.CheckSource(badFieldScript, "bad_field.cameo");
        Assert.That(fieldErrors, Is.Not.Empty);
        Assert.That(fieldErrors[0].File, Is.EqualTo("bad_field.cameo"));
        Assert.That(fieldErrors[0].Line, Is.GreaterThan(0));

        string badSyntaxScript = "rule({ code = 'X' }, (ctx: LintContext) => { let = })";
        var syntaxErrors = CameoLintRulePack.CheckSource(badSyntaxScript, "bad_syntax.cameo");
        Assert.That(syntaxErrors, Is.Not.Empty);

        Assert.Throws<CameoLintScriptException>(() => CameoLintRulePack.LoadSource(badFieldScript, "bad_field.cameo"));
    }

    [Test]
    public void HermeticSandboxBlocksFrozenMutationFilesystemAccessAndInfiniteLoops()
    {
        string simpleIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "a", "width": 8, "direction": "input"},
                {"name": "y", "width": 8, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "y"}, "value": {"kind": "ref", "name": "a"}}
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        // 1. Mutating frozen ctx.signals must fail cleanly as CAMEOERROR
        string mutateScript = """
            rule({
                code = "TRY_MUTATE",
                summary = "Attempt to mutate frozen context."
            }, (ctx: LintContext) => {
                ctx.signals.clear()
            })
            """;
        var mutatePack = CameoLintRulePack.LoadSource(mutateScript, "mutate.cameo");
        var mutateReport = RtlLinter.LintJson(simpleIr, new LintOptions(CustomRules: mutatePack.Rules));
        Assert.That(mutateReport.Diagnostics.Any(d => d.Code == "CAMEOERROR" && d.Message.Contains("frozen", StringComparison.OrdinalIgnoreCase)),
            Is.True, mutateReport.FormatText());

        // 2. Infinite loop must abort deterministically via MaxSteps and report CAMEOERROR
        string loopScript = """
            rule({
                code = "INF_LOOP",
                summary = "Infinite loop bounded by MaxSteps."
            }, (ctx: LintContext) => {
                var i = 0
                while i >= 0 {
                    i = i + 1
                }
            })
            """;
        var loopPack = CameoLintRulePack.LoadSource(loopScript, "loop.cameo", maxSteps: 2_000);
        var loopReport = RtlLinter.LintJson(simpleIr, new LintOptions(CustomRules: loopPack.Rules));
        Assert.That(loopReport.Diagnostics.Any(d => d.Code == "CAMEOERROR" && d.Message.Contains("StepLimit", StringComparison.OrdinalIgnoreCase)),
            Is.True, loopReport.FormatText());

        // 3. Hermetic runtime denies io.open (io is absent in CameoLibraries.Hermetic)
        string ioScript = """
            rule({
                code = "TRY_IO",
                summary = "Attempt filesystem access in hermetic mode."
            }, (ctx: LintContext) => {
                let f = io.open("/etc/passwd", "r")
                if f != nil {
                    ctx.report("TRY_IO", "error", "should not open")
                }
            })
            """;
        var ioPack = CameoLintRulePack.LoadSource(ioScript, "io.cameo");
        var ioReport = RtlLinter.LintJson(simpleIr, new LintOptions(CustomRules: ioPack.Rules));
        Assert.That(ioReport.Diagnostics.Any(d => d.Code == "CAMEOERROR"), Is.True, ioReport.FormatText());
    }

    [Test]
    public async Task ConfigRuleScriptsWaiversAndToolCliCheckRulesListRulesWorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "velosim-cameo-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string rulePath = Path.Combine(tempDir, "team_rules.cameo");
            string badRulePath = Path.Combine(tempDir, "broken.cameo");
            string cfgPath = Path.Combine(tempDir, "velosim-lint.json");
            string irPath = Path.Combine(tempDir, "design.rtl.json");

            await File.WriteAllTextAsync(rulePath, """
                rule({
                    code = "TEAM_NO_WIDE_SIG",
                    phase = "style",
                    severity = "warning",
                    standards = ["TEAM-01"],
                    summary = "Signals wider than 64 bits require waiver."
                }, (ctx: LintContext) => {
                    for s in ctx.signals {
                        if s.width > 64 {
                            ctx.report("TEAM_NO_WIDE_SIG", "warning", $"Signal '{s.name}' width {s.width} > 64", s.name, "decl")
                        }
                    }
                })
                """);

            await File.WriteAllTextAsync(badRulePath, """
                rule({
                    code = "BROKEN",
                    summary = "Bad type."
                }, (ctx: LintContext) => {
                    for s in ctx.signals {
                        if s.bad_prop > 0 { ctx.report("BROKEN", "error", "x") }
                    }
                })
                """);

            await File.WriteAllTextAsync(cfgPath, """
                {
                  "schema": "velosim.lint.config.v1",
                  "profile": "wall",
                  "rule_scripts": ["team_rules.cameo"],
                  "cameo_max_steps": 100000,
                  "rules": {
                    "TEAM_NO_WIDE_SIG": "error"
                  },
                  "waivers": [
                    {
                      "id": "W-WIDE-OK",
                      "code": "TEAM_NO_WIDE_SIG",
                      "signal": "waived_bus"
                    }
                  ]
                }
                """);

            await File.WriteAllTextAsync(irPath, """
                {
                  "format": "velosim.rtl.v1",
                  "signals": [
                    {"name": "waived_bus", "width": 128, "direction": "input"},
                    {"name": "unwaived_bus", "width": 128, "direction": "output"}
                  ],
                  "memories": [],
                  "continuous": [
                    {"target": {"kind": "ref", "name": "unwaived_bus"}, "value": {"kind": "ref", "name": "waived_bus"}}
                  ],
                  "clock": null,
                  "body": {"kind": "block", "items": []}
                }
                """);

            // 1. --check-rules passes on valid pack
            using (var outW = new StringWriter())
            using (var errW = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["lint", "--check-rules", "--rules", rulePath], outW, errW);
                Assert.That(rc, Is.EqualTo(0), outW.ToString() + errW.ToString());
                Assert.That(outW.ToString(), Does.Contain("ok:"));
            }

            // 2. --check-rules fails on broken pack
            using (var outW = new StringWriter())
            using (var errW = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["lint", "--check-rules", "--rules", badRulePath], outW, errW);
                Assert.That(rc, Is.EqualTo(1));
                Assert.That(outW.ToString(), Does.Contain("error:"));
            }

            // 3. --list-rules --rules <pack> --json exposes 45 built-in + 1 Cameo = 46 rules
            using (var outW = new StringWriter())
            using (var errW = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["lint", "--list-rules", "--rules", rulePath, "--json"], outW, errW);
                Assert.That(rc, Is.EqualTo(0));
                using var doc = JsonDocument.Parse(outW.ToString());
                var arr = doc.RootElement.GetProperty("rules");
                Assert.That(arr.GetArrayLength(), Is.EqualTo(46));
                Assert.That(arr.EnumerateArray().Any(r => r.GetProperty("code").GetString() == "TEAM_NO_WIDE_SIG"), Is.True);
            }

            // 4. End-to-end lint using discovered velosim-lint.json (relative rule_scripts, severity promotion to error, waiver on waived_bus)
            using (var outW = new StringWriter())
            using (var errW = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["lint", irPath, "--config", cfgPath, "--cameo-max-steps", "200000"], outW, errW);
                Assert.That(rc, Is.EqualTo(1));
                string text = outW.ToString();
                Assert.That(text, Does.Contain("%Error-TEAM_NO_WIDE_SIG"));
                Assert.That(text, Does.Contain("'unwaived_bus'"));
                Assert.That(text, Does.Not.Contain("'waived_bus'"));
                Assert.That(text, Does.Contain("1 waived"));
            }

            // 5. --init-rule scaffolds a statically valid Cameo rule pack, and --emit-cameo-prelude emits velosim_lint.d.tl
            string initRuleOut = Path.Combine(tempDir, "starter.cameo");
            string preludeOut = Path.Combine(tempDir, "velosim_lint.d.tl");
            using (var outW = new StringWriter())
            using (var errW = new StringWriter())
            {
                Assert.That(await ToolCli.RunAsync(["lint", "--init-rule", initRuleOut], outW, errW), Is.EqualTo(0));
                Assert.That(await ToolCli.RunAsync(["lint", "--emit-cameo-prelude", preludeOut], outW, errW), Is.EqualTo(0));
            }
            Assert.That(CameoLintRulePack.CheckFile(initRuleOut), Is.Empty);
            Assert.That(await File.ReadAllTextAsync(preludeOut), Does.Contain("global record LintContext"));

            // 6. Parameterized Cameo rules via cameo_params + rtl.glob / rtl.matches / rtl.is_pow2 / rtl.clog2
            string paramCfgPath = Path.Combine(tempDir, "param-lint.json");
            await File.WriteAllTextAsync(paramCfgPath, """
                {
                  "schema": "velosim.lint.config.v1",
                  "profile": "wall",
                  "rule_scripts": ["starter.cameo"],
                  "cameo_params": {
                    "max_bus_width": "64"
                  }
                }
                """);
            using (var outW = new StringWriter())
            using (var errW = new StringWriter())
            {
                int rc = await ToolCli.RunAsync(["lint", irPath, "--config", paramCfgPath], outW, errW);
                Assert.That(rc, Is.EqualTo(0));
                Assert.That(outW.ToString(), Does.Contain("CUSTOM_EXAMPLE"));
                Assert.That(outW.ToString(), Does.Contain("max_bus_width (64)"));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
