using System.IO.Compression;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlLinterTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "VeloSim.slnx")))
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("Could not locate VeloSim.slnx");
        return dir;
    }

    private static string LoadRv32iAstJson()
    {
        string gzPath = Path.Combine(RepoRoot(), "tests", "VeloSim.Tests", "Fixtures", "rv32i_core.slang.ast.json.gz");
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        return sr.ReadToEnd();
    }

    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, string, ProcessResult> handler) : IProcessRunner
    {
        public List<(string Exe, IReadOnlyList<string> Args)> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Invocations.Add((executable, arguments.ToArray()));
            return Task.FromResult(handler(executable, arguments, workingDirectory));
        }
    }

    private static string CleanCounterIr() => """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "rst", "width": 1, "direction": "input"},
            {"name": "q", "width": 8, "direction": "output"}
          ],
          "memories": [],
          "continuous": [],
          "clock": "clk",
          "body": {
            "kind": "if",
            "condition": {"kind": "ref", "name": "rst"},
            "then": {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "const", "bits": "00000000"}},
            "else": {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "q"}, {"kind": "const", "bits": "00000001"}]}}
          }
        }
        """;

    [Test]
    public void CleanCounterPassesUnderWallAndWerror()
    {
        var report = RtlLinter.LintJson(CleanCounterIr(), new LintOptions(Wall: true, WarningsAsErrors: true));
        Assert.That(report.Passed, Is.True, report.FormatText());
        Assert.That(report.ErrorCount, Is.Zero);
        Assert.That(report.WarningCount, Is.Zero);
        Assert.That(report.Diagnostics, Is.Empty);
        Assert.That(report.FormatText(), Does.Contain("lint pass: 0 error(s), 0 warning(s)"));

        using var doc = JsonDocument.Parse(report.ToJson());
        Assert.That(doc.RootElement.GetProperty("schema").GetString(), Is.EqualTo("velosim.lint.v1"));
        Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("pass"));
        Assert.That(doc.RootElement.GetProperty("wall").GetBoolean(), Is.True);
    }

    [Test]
    public void LintDesignEntryPointMatchesLintJson()
    {
        var design = RtlDesign.Parse(CleanCounterIr());
        var report = RtlLinter.LintDesign(design, LintOptions.All);
        Assert.That(report.Passed, Is.True);
        Assert.That(report.Diagnostics, Is.Empty);
    }

    [Test]
    public void DetectsWidthTruncAndWidthExpandWithDefaultVsWallAndGroupSuppression()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "a", "width": 16, "direction": "input"},
                {"name": "b", "width": 4, "direction": "input"},
                {"name": "narrow_out", "width": 8, "direction": "output"},
                {"name": "wide_out", "width": 12, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "narrow_out"}, "value": {"kind": "ref", "name": "a"}},
                {"target": {"kind": "ref", "name": "wide_out"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "b"}, {"kind": "const", "bits": "00000001"}]}}
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        // Default: WIDTHTRUNC is enabled, WIDTHEXPAND is off
        var defReport = RtlLinter.LintJson(ir, LintOptions.Default);
        Assert.That(defReport.Diagnostics.Select(d => d.Code), Contains.Item("WIDTHTRUNC"));
        Assert.That(defReport.Diagnostics.Select(d => d.Code), Does.Not.Contain("WIDTHEXPAND"));
        Assert.That(defReport.Passed, Is.True); // warnings don't fail without -Werror

        // -Wall: both WIDTHTRUNC and WIDTHEXPAND are reported
        var wallReport = RtlLinter.LintJson(ir, LintOptions.All);
        Assert.That(wallReport.Diagnostics.Select(d => d.Code), Contains.Item("WIDTHTRUNC"));
        Assert.That(wallReport.Diagnostics.Select(d => d.Code), Contains.Item("WIDTHEXPAND"));

        // -Wall -Werror fails; -Wall -Werror -Wno-fatal passes
        var werrorReport = RtlLinter.LintJson(ir, new LintOptions(Wall: true, WarningsAsErrors: true));
        Assert.That(werrorReport.Passed, Is.False);
        Assert.That(werrorReport.ExitCode, Is.EqualTo(1));
        Assert.That(werrorReport.ErrorCount, Is.GreaterThanOrEqualTo(2));

        var noFatalReport = RtlLinter.LintJson(ir, new LintOptions(Wall: true, WarningsAsErrors: true, NoFatal: true));
        Assert.That(noFatalReport.Passed, Is.True);
        Assert.That(noFatalReport.ExitCode, Is.Zero);
        Assert.That(noFatalReport.WarningCount, Is.GreaterThanOrEqualTo(2));

        // -Wall -Wno-WIDTH suppresses both WIDTHTRUNC and WIDTHEXPAND
        var suppressed = RtlLinter.LintJson(ir, new LintOptions(Wall: true, DisabledRules: new HashSet<string> { "WIDTH" }));
        Assert.That(suppressed.Diagnostics, Is.Empty);
    }

    [Test]
    public void DetectsUnusedSignalsAndMemoriesUnderWallOnly()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "used_in", "width": 8, "direction": "input"},
                {"name": "unused_in", "width": 8, "direction": "input"},
                {"name": "dead_internal", "width": 8, "direction": "internal"},
                {"name": "out_q", "width": 8, "direction": "output"}
              ],
              "memories": [
                {"name": "unused_mem", "width": 8, "depth": 4}
              ],
              "continuous": [
                {"target": {"kind": "ref", "name": "dead_internal"}, "value": {"kind": "ref", "name": "used_in"}}
              ],
              "clock": "clk",
              "body": {
                "kind": "nba",
                "target": {"kind": "ref", "name": "out_q"},
                "value": {"kind": "ref", "name": "used_in"}
              }
            }
            """;

        var defReport = RtlLinter.LintJson(ir, LintOptions.Default);
        Assert.That(defReport.Diagnostics, Is.Empty);

        var wallReport = RtlLinter.LintJson(ir, LintOptions.All);
        var codes = wallReport.Diagnostics.Select(d => (d.Code, d.Location?.Signal)).ToArray();
        Assert.That(codes, Contains.Item(("UNUSEDSIGNAL", "unused_in")));
        Assert.That(codes, Contains.Item(("UNUSEDSIGNAL", "dead_internal")));
        Assert.That(codes, Contains.Item(("UNUSEDMEM", "unused_mem")));
        Assert.That(codes.Select(c => c.Signal), Does.Not.Contain("clk"));
        Assert.That(codes.Select(c => c.Signal), Does.Not.Contain("out_q"));

        // Suppress group UNUSED
        var noUnused = RtlLinter.LintJson(ir, new LintOptions(Wall: true, DisabledRules: new HashSet<string> { "UNUSED" }));
        Assert.That(noUnused.Diagnostics, Is.Empty);
    }

    [Test]
    public void DetectsLatchFeedbackMultiDriverClkCombBlkAndNblkAndUndrivenInSinglePass()
    {
        // Deliberately combines multiple structural bugs in one IR: RtlDesign.Parse would stop on the first;
        // RtlLinter collects ALL of them in one pass!
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "en", "width": 1, "direction": "input"},
                {"name": "latch_sig", "width": 8, "direction": "output"},
                {"name": "loop_a", "width": 8, "direction": "output"},
                {"name": "loop_b", "width": 8, "direction": "output"},
                {"name": "clk_comb_out", "width": 1, "direction": "output"},
                {"name": "mixed_sig", "width": 8, "direction": "output"},
                {"name": "undriven_out", "width": 8, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "loop_a"}, "value": {"kind": "ref", "name": "loop_b"}},
                {"target": {"kind": "ref", "name": "loop_b"}, "value": {"kind": "ref", "name": "loop_a"}},
                {"target": {"kind": "ref", "name": "clk_comb_out"}, "value": {"kind": "ref", "name": "clk"}},
                {"target": {"kind": "ref", "name": "mixed_sig"}, "value": {"kind": "const", "bits": "00000001"}}
              ],
              "combinational": [
                {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "en"},
                  "then": {"kind": "blocking", "target": {"kind": "ref", "name": "latch_sig"}, "value": {"kind": "const", "bits": "11111111"}},
                  "else": {"kind": "block", "items": []}
                }
              ],
              "clock": "clk",
              "body": {
                "kind": "block",
                "items": [
                  {"kind": "blocking", "target": {"kind": "ref", "name": "mixed_sig"}, "value": {"kind": "const", "bits": "00000010"}},
                  {"kind": "nba", "target": {"kind": "ref", "name": "mixed_sig"}, "value": {"kind": "const", "bits": "00000011"}}
                ]
              }
            }
            """;

        var report = RtlLinter.LintJson(ir, LintOptions.All);
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.That(report.Passed, Is.False);
        Assert.That(codes, Contains.Item("LATCH"), report.FormatText());
        Assert.That(codes, Contains.Item("UNOPTFLAT"), report.FormatText());
        Assert.That(codes, Contains.Item("CLKCOMB"), report.FormatText());
        Assert.That(codes, Contains.Item("MULTIDRIVEN"), report.FormatText());
        Assert.That(codes, Contains.Item("BLKANDNBLK"), report.FormatText());
        Assert.That(codes, Contains.Item("BLKSEQ"), report.FormatText());
        Assert.That(codes, Contains.Item("UNDRIVEN"), report.FormatText());
    }

    [Test]
    public void DetectsCaseIncompleteOverlapCasexAndPassesCompleteCase()
    {
        string buggyCaseIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "sel", "width": 2, "direction": "input"},
                {"name": "y", "width": 4, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "combinational": [
                {
                  "kind": "block",
                  "items": [
                    {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0000"}},
                    {
                      "kind": "case",
                      "case_kind": "casex",
                      "value": {"kind": "ref", "name": "sel"},
                      "items": [
                        {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0001"}}},
                        {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0010"}}}
                      ]
                    }
                  ]
                }
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        var report = RtlLinter.LintJson(buggyCaseIr, LintOptions.All);
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.That(codes, Contains.Item("CASEX"));
        Assert.That(codes, Contains.Item("CASEOVERLAP"));
        Assert.That(codes, Contains.Item("CASEINCOMPLETE"));

        // Exhaustive 2-bit exact case (all 4 states covered) should NOT trigger CASEINCOMPLETE
        string completeCaseIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "sel", "width": 2, "direction": "input"},
                {"name": "y", "width": 4, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "combinational": [
                {
                  "kind": "case",
                  "value": {"kind": "ref", "name": "sel"},
                  "items": [
                    {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0001"}}},
                    {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0010"}}},
                    {"match": {"kind": "const", "bits": "10"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0100"}}},
                    {"match": {"kind": "const", "bits": "11"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "1000"}}}
                  ]
                }
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        var okReport = RtlLinter.LintJson(completeCaseIr, LintOptions.All);
        Assert.That(okReport.Diagnostics.Select(d => d.Code), Does.Not.Contain("CASEINCOMPLETE"));
        Assert.That(okReport.Diagnostics.Select(d => d.Code), Does.Not.Contain("CASEOVERLAP"));
        Assert.That(okReport.Diagnostics.Select(d => d.Code), Does.Not.Contain("CASEX"));
    }

    [Test]
    public void DetectsSignCompareSelfAssignCmpConstSelRangeAndFourStateHazards()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "s_in", "width": 8, "direction": "input", "signed": true},
                {"name": "u_in", "width": 8, "direction": "input"},
                {"name": "cmp_out", "width": 1, "direction": "output"},
                {"name": "xz_out", "width": 1, "direction": "output"},
                {"name": "const_lt", "width": 1, "direction": "output"},
                {"name": "sel_oob", "width": 1, "direction": "output"},
                {"name": "two_s", "width": 8, "direction": "output", "two_state": true},
                {"name": "self_q", "width": 8, "direction": "output"}
              ],
              "memories": [
                {"name": "rom", "width": 8, "depth": 4}
              ],
              "continuous": [
                {"target": {"kind": "ref", "name": "cmp_out"}, "value": {"kind": "lt", "args": [{"kind": "ref", "name": "s_in"}, {"kind": "ref", "name": "u_in"}]}},
                {"target": {"kind": "ref", "name": "xz_out"}, "value": {"kind": "eq", "args": [{"kind": "ref", "name": "u_in"}, {"kind": "const", "bits": "xxxxxxxx"}]}},
                {"target": {"kind": "ref", "name": "const_lt"}, "value": {"kind": "lt", "args": [{"kind": "ref", "name": "u_in"}, {"kind": "const", "bits": "00000000"}]}},
                {"target": {"kind": "ref", "name": "sel_oob"}, "value": {"kind": "bitselect", "value": {"kind": "ref", "name": "u_in"}, "index": {"kind": "const", "bits": "1000"}}},
                {"target": {"kind": "ref", "name": "two_s"}, "value": {"kind": "index", "name": "rom", "index": {"kind": "const", "bits": "100"}}}
              ],
              "clock": "clk",
              "body": {
                "kind": "if",
                "condition": {"kind": "const", "bits": "1"},
                "then": {"kind": "nba", "target": {"kind": "ref", "name": "self_q"}, "value": {"kind": "ref", "name": "self_q"}},
                "else": {"kind": "block", "items": []}
              }
            }
            """;

        var report = RtlLinter.LintJson(ir, LintOptions.All);
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.That(codes, Contains.Item("SIGNCOMPARE"), report.FormatText());
        Assert.That(codes, Contains.Item("FOURSTATE"), report.FormatText());
        Assert.That(codes, Contains.Item("CMPCONST"), report.FormatText());
        Assert.That(codes, Contains.Item("SELRANGE"), report.FormatText());
        Assert.That(codes, Contains.Item("SELFASSIGN"), report.FormatText());
        // Both bitselect [8] on 8-bit u_in AND rom[4] on depth-4 rom should fire SELRANGE
        Assert.That(report.Diagnostics.Count(d => d.Code == "SELRANGE"), Is.EqualTo(2));
        // Both == 8'bxxxx_xxxx AND 4-state rom assigned to two_s should fire FOURSTATE
        Assert.That(report.Diagnostics.Count(d => d.Code == "FOURSTATE"), Is.EqualTo(2));
    }

    [Test]
    public void SurfacesTimedMultiProcessRaceHazardsAsStructuredDiagnostics()
    {
        string timedIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "shared", "width": 8, "direction": "output"},
                {"name": "obs", "width": 8, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "clock": null,
              "body": {"kind": "block", "items": []},
              "timed": [
                {"kind": "blocking", "target": {"kind": "ref", "name": "shared"}, "value": {"kind": "const", "bits": "00000001"}},
                {
                  "kind": "block",
                  "items": [
                    {"kind": "blocking", "target": {"kind": "ref", "name": "shared"}, "value": {"kind": "const", "bits": "00000010"}},
                    {"kind": "blocking", "target": {"kind": "ref", "name": "obs"}, "value": {"kind": "ref", "name": "shared"}}
                  ]
                }
              ]
            }
            """;

        var report = RtlLinter.LintJson(timedIr, LintOptions.Default);
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.That(codes, Contains.Item("RACEWW"), report.FormatText());
        Assert.That(codes, Contains.Item("RACERW"), report.FormatText());
        Assert.That(report.Passed, Is.True);

        // Promoting RACE to error via -Werror-RACE fails
        var promoted = RtlLinter.LintJson(timedIr, new LintOptions(ErrorRules: new HashSet<string> { "RACE" }));
        Assert.That(promoted.Passed, Is.False);
        Assert.That(promoted.Diagnostics.All(d => d.Severity == LintSeverity.Error), Is.True);
    }

    [Test]
    public void MalformedJsonAndBadFormatReportValidErrorWithoutThrowing()
    {
        var malformed = RtlLinter.LintJson("{not valid json");
        Assert.That(malformed.Passed, Is.False);
        Assert.That(malformed.Diagnostics.Select(d => d.Code), Contains.Item("VALIDERROR"));

        var wrongFmt = RtlLinter.LintJson("{\"format\":\"other.v1\"}");
        Assert.That(wrongFmt.Passed, Is.False);
        Assert.That(wrongFmt.Diagnostics.Select(d => d.Code), Contains.Item("VALIDERROR"));
    }

    [Test]
    public void RealRv32iCoreLoweredFromSlangAstHasZeroErrorsAndZeroWarningsUnderWall()
    {
        string astJson = LoadRv32iAstJson();
        string irJson = SlangAstLowerer.LowerAstJson(astJson, "rv32i_core");
        var report = RtlLinter.LintJson(irJson, new LintOptions(Wall: true, WarningsAsErrors: true), "examples/rv32i_core.sv");
        Assert.That(report.Passed, Is.True, report.FormatText());
        Assert.That(report.ErrorCount, Is.Zero, report.FormatText());
        Assert.That(report.WarningCount, Is.Zero, report.FormatText());
    }

    [Test]
    public void DetectsCasezExhaustionCondChecksPartselectRangeAndSignAssignment()
    {
        // Exhaustive 2-bit casez (2'b1?, 2'b01, 2'b00) covers all 4 states -> no CASEINCOMPLETE
        string casezIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "sel", "width": 2, "direction": "input"},
                {"name": "y", "width": 4, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "combinational": [
                {
                  "kind": "case",
                  "case_kind": "casez",
                  "value": {"kind": "ref", "name": "sel"},
                  "items": [
                    {"match": {"kind": "const", "bits": "1z"}, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0001"}}},
                    {"matches": [{"kind": "const", "bits": "01"}, {"kind": "const", "bits": "00"}], "body": {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "const", "bits": "0010"}}}
                  ]
                }
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;
        var casezReport = RtlLinter.LintJson(casezIr, LintOptions.All);
        Assert.That(casezReport.Diagnostics.Select(d => d.Code), Does.Not.Contain("CASEINCOMPLETE"), casezReport.FormatText());

        // Cond constant selector + mismatched branch widths/signs + partselect OOB + signed->unsigned assignment + unsigned cmp > max
        string miscIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "s", "width": 8, "direction": "input", "signed": true},
                {"name": "u4", "width": 4, "direction": "input"},
                {"name": "out_u", "width": 8, "direction": "output"},
                {"name": "out_cond", "width": 8, "direction": "output"},
                {"name": "out_ps", "width": 4, "direction": "output"},
                {"name": "out_cmp", "width": 1, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "out_u"}, "value": {"kind": "ref", "name": "s"}},
                {"target": {"kind": "ref", "name": "out_cond"}, "value": {"kind": "cond", "args": [{"kind": "const", "bits": "1"}, {"kind": "ref", "name": "s"}, {"kind": "ref", "name": "u4"}]}},
                {"target": {"kind": "ref", "name": "out_ps"}, "value": {"kind": "partselect", "value": {"kind": "ref", "name": "u4"}, "left": 5, "right": 2}},
                {"target": {"kind": "ref", "name": "out_cmp"}, "value": {"kind": "eq", "args": [{"kind": "ref", "name": "u4"}, {"kind": "const", "bits": "11111111"}]}}
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;
        var miscReport = RtlLinter.LintJson(miscIr, LintOptions.All);
        var codes = miscReport.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.That(codes, Contains.Item("SIGNCOMPARE"), miscReport.FormatText());
        Assert.That(codes, Contains.Item("CMPCONST"), miscReport.FormatText());
        Assert.That(codes, Contains.Item("WIDTHEXPAND"), miscReport.FormatText());
        Assert.That(codes, Contains.Item("SELRANGE"), miscReport.FormatText());
        Assert.That(LintRuleCatalog.TryGetRule("WIDTHTRUNC", out var info), Is.True);
        Assert.That(info.Group, Is.EqualTo("WIDTH"));
    }

    [Test]
    public void LintsParallelProcessesConcatTargetsInitializationAndVerificationReads()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "din", "width": 8, "direction": "input"},
                {"name": "prop_sig", "width": 1, "direction": "input"},
                {"name": "cg_sig", "width": 4, "direction": "input"},
                {"name": "hi", "width": 4, "direction": "output"},
                {"name": "lo", "width": 4, "direction": "output"},
                {"name": "shared_q", "width": 8, "direction": "output"},
                {"name": "init_only", "width": 8, "direction": "output"}
              ],
              "memories": [
                {"name": "ram", "width": 8, "depth": 4}
              ],
              "continuous": [],
              "initialization": {
                "kind": "block",
                "items": [
                  {"kind": "blocking", "target": {"kind": "ref", "name": "init_only"}, "value": {"kind": "const", "bits": "00000000"}},
                  {"kind": "blocking", "target": {"kind": "index", "name": "ram", "index": {"kind": "const", "bits": "00"}}, "value": {"kind": "const", "bits": "00000001"}}
                ]
              },
              "clock": "clk",
              "body": {
                "kind": "parallel",
                "items": [
                  {
                    "kind": "process",
                    "events": [{"signal": "clk", "edge": "posedge"}],
                    "body": {
                      "kind": "block",
                      "items": [
                        {"kind": "nba", "target": {"kind": "concat", "args": [{"kind": "ref", "name": "hi"}, {"kind": "ref", "name": "lo"}]}, "value": {"kind": "ref", "name": "din"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "shared_q"}, "value": {"kind": "index", "name": "ram", "index": {"kind": "const", "bits": "00"}}},
                        {"kind": "nba", "target": {"kind": "bitselect", "value": {"kind": "ref", "name": "lo"}, "index": {"kind": "const", "bits": "00"}}, "value": {"kind": "ref", "name": "rst"}}
                      ]
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk", "edge": "posedge"}],
                    "body": {
                      "kind": "nba",
                      "target": {"kind": "ref", "name": "shared_q"},
                      "value": {"kind": "convert", "width": 8, "signed": false, "value": {"kind": "inside", "value": {"kind": "ref", "name": "din"}, "items": [{"lo": {"kind": "const", "bits": "00000000"}, "hi": {"kind": "const", "bits": "00001111"}}, {"kind": "const", "bits": "11111111"}]}}
                    }
                  }
                ]
              },
              "properties": [
                {"name": "p0", "kind": "assert", "antecedent": {"kind": "rose", "signal": "prop_sig"}, "consequent": {"kind": "past", "signal": "prop_sig", "ticks": 1}}
              ],
              "covergroups": [
                {"name": "cg0", "coverpoints": [{"name": "cp0", "expr": {"kind": "ref", "name": "cg_sig"}}]}
              ]
            }
            """;

        var report = RtlLinter.LintJson(ir, LintOptions.All);
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.That(codes, Contains.Item("MULTIDRIVEN"), report.FormatText());
        // prop_sig and cg_sig are read by properties/covergroups so must NOT be flagged UNUSEDSIGNAL
        Assert.That(report.Diagnostics.Any(d => d.Code == "UNUSEDSIGNAL" && d.Location?.Signal is "prop_sig" or "cg_sig"), Is.False, report.FormatText());
    }

    [Test]
    public void LintsTimedStatementsContainersLoopsAndReadBeforeAssignLatch()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "a", "width": 8, "direction": "input"},
                {"name": "y", "width": 8, "direction": "output"},
                {"name": "flag", "width": 1, "direction": "output"}
              ],
              "memories": [
                {"name": "qmem", "width": 8, "depth": 8, "queue": true},
                {"name": "dmem", "width": 8, "depth": 8, "dynamic": true},
                {"name": "amem", "width": 8, "depth": 8, "associative": true, "key_width": 8}
              ],
              "continuous": [],
              "combinational": [
                {
                  "kind": "block",
                  "items": [
                    {"kind": "blocking", "target": {"kind": "ref", "name": "y"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "y"}, {"kind": "ref", "name": "a"}]}},
                    {"kind": "repeat", "count": 1, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "flag"}, "value": {"kind": "const", "bits": "1"}}}
                  ]
                }
              ],
              "clock": null,
              "body": {"kind": "block", "items": []}
            }
            """;

        // Reading owned signal 'y' before definite assignment in combinational block triggers LATCH
        var combLatch = RtlLinter.LintJson(ir, LintOptions.All);
        Assert.That(combLatch.Diagnostics.Select(d => d.Code), Contains.Item("LATCH"), combLatch.FormatText());

        // Timed IR exercising always_star, delay, wait, fork, event, while, for, assert, display, fatal, force, value_plusargs, containers, urandom_range
        string timedIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "trig", "width": 1, "direction": "input"},
                {"name": "a", "width": 8, "direction": "input"},
                {"name": "out_v", "width": 8, "direction": "output"},
                {"name": "k", "width": 8, "direction": "output"},
                {"name": "found", "width": 1, "direction": "output"},
                {"name": "vp", "width": 8, "direction": "output"},
                {"name": "vpm", "width": 1, "direction": "output"}
              ],
              "memories": [
                {"name": "qmem", "width": 8, "depth": 8, "queue": true},
                {"name": "dmem", "width": 8, "depth": 8, "dynamic": true},
                {"name": "amem", "width": 8, "depth": 8, "associative": true, "key_width": 8}
              ],
              "continuous": [],
              "clock": null,
              "body": {"kind": "block", "items": []},
              "timed": [
                {
                  "kind": "always_star",
                  "body": {"kind": "blocking", "target": {"kind": "ref", "name": "out_v"}, "value": {"kind": "ref", "name": "a"}}
                },
                {
                  "kind": "fork",
                  "items": [
                    {
                      "kind": "delay",
                      "ticks": 1,
                      "body": {
                        "kind": "wait",
                        "condition": {"kind": "ref", "name": "trig"},
                        "body": {
                          "kind": "event",
                          "events": [{"signal": "trig", "edge": "posedge"}],
                          "body": {
                            "kind": "block",
                            "items": [
                              {"kind": "allocate", "name": "dmem", "count": {"kind": "const", "bits": "00000100"}, "preserve": false},
                              {"kind": "queue_push", "name": "qmem", "value": {"kind": "ref", "name": "a"}},
                              {"kind": "queue_pop", "name": "qmem", "target": "k"},
                              {"kind": "delete", "name": "dmem"},
                              {"kind": "assoc_traverse", "name": "amem", "key": "k", "found": "found"},
                              {"kind": "assert", "condition": {"kind": "ref", "name": "trig"}},
                              {"kind": "display", "format": "%d", "args": [{"kind": "urandom_range", "max": {"kind": "const", "bits": "00001111"}, "min": {"kind": "const", "bits": "00000001"}}]},
                              {"kind": "value_plusargs", "format": "VAL=%d", "target": "vp", "matched": "vpm"},
                              {"kind": "force", "target": "out_v", "value": {"kind": "ref", "name": "a"}},
                              {"kind": "while", "condition": {"kind": "const", "bits": "0"}, "body": {"kind": "block", "items": []}},
                              {"kind": "for", "condition": {"kind": "const", "bits": "0"}, "step": {"kind": "block", "items": []}, "body": {"kind": "block", "items": []}},
                              {"kind": "fatal", "code": 1, "format": "%d", "args": [{"kind": "size", "name": "qmem"}]}
                            ]
                          }
                        }
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var timedReport = RtlLinter.LintJson(timedIr, LintOptions.All);
        // Should cleanly traverse all timed/container/system statements without throwing
        Assert.That(timedReport.Diagnostics.Select(d => d.Code), Does.Not.Contain("UNDRIVEN"), timedReport.FormatText());
    }

    [Test]
    public void SlangLinterBuildsExpectedArgumentsAndParsesTextAndJsonDiagnostics()
    {
        var opts = new LintOptions(
            Wall: true,
            WarningsAsErrors: true,
            EnabledRules: new HashSet<string> { "IMPLICIT", "custom-warn" },
            DisabledRules: new HashSet<string> { "WIDTHEXPAND", "FOURSTATE" },
            ErrorRules: new HashSet<string> { "LATCH" },
            IncludeDirectories: ["inc"],
            Defines: ["SYNTHESIS=1"]);

        var args = SlangLinter.BuildSlangArguments(["design.sv"], opts, top: "top_mod", astJsonPath: null);
        Assert.That(args, Contains.Item("--std"));
        Assert.That(args, Contains.Item("1800-2023"));
        Assert.That(args, Contains.Item("--lint-only"));
        Assert.That(args, Contains.Item("--top"));
        Assert.That(args, Contains.Item("top_mod"));
        Assert.That(args, Contains.Item("-Weverything"));
        Assert.That(args, Contains.Item("-Werror"));
        Assert.That(args, Contains.Item("-Wimplicit-net"));
        Assert.That(args, Contains.Item("-Wcustom-warn"));
        Assert.That(args, Contains.Item("-Wno-width-expand"));
        Assert.That(args, Does.Not.Contain("-Wno-FOURSTATE")); // VeloSim-only rule is not forwarded to slang
        Assert.That(args, Contains.Item("-Werror=inferred-latch"));
        Assert.That(args, Contains.Item("-I"));
        Assert.That(args, Contains.Item("-D"));
        Assert.That(args, Contains.Item("SYNTHESIS=1"));

        // Text parsing (including ANSI codes and Windows paths)
        string sample =
            "\x1B[1mdesign.sv:12:5: \x1B[35mwarning:\x1B[0m implicit conversion truncates from 16 to 8 bits [-Wwidth-trunc]\n" +
            "C:\\rtl\\sub.sv:44:9: error: use of undeclared identifier 'bar'\n" +
            "design.sv:10:1: note: declared here\n";
        var parsed = SlangLinter.ParseSlangOutput(sample);
        Assert.That(parsed.Count, Is.EqualTo(3));
        Assert.That(parsed[0].Code, Is.EqualTo("WIDTHTRUNC"));
        Assert.That(parsed[0].Severity, Is.EqualTo(LintSeverity.Warning));
        Assert.That(parsed[0].Location?.File, Is.EqualTo("design.sv"));
        Assert.That(parsed[0].Location?.Line, Is.EqualTo(12));
        Assert.That(parsed[0].Location?.Column, Is.EqualTo(5));
        Assert.That(parsed[1].Code, Is.EqualTo("SLANGERROR"));
        Assert.That(parsed[1].Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(parsed[1].Location?.File, Is.EqualTo("C:\\rtl\\sub.sv"));
        Assert.That(parsed[2].Severity, Is.EqualTo(LintSeverity.Info));

        // JSON parsing (both array and wrapped {"diagnostics": [...]})
        string jsonDiags = """
            {"diagnostics":[
              {"severity":"warning","message":"unused variable 'x'","optionName":"unused-variable","location":{"file":"a.sv","line":3,"column":7}},
              {"severity":"error","message":"syntax error","file":"a.sv","line":9,"column":1}
            ]}
            """;
        var parsedJson = SlangLinter.ParseSlangDiagnosticsJson(jsonDiags);
        Assert.That(parsedJson.Count, Is.EqualTo(2));
        Assert.That(parsedJson[0].Code, Is.EqualTo("UNUSEDSIGNAL"));
        Assert.That(parsedJson[1].Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(parsedJson[1].Location?.Line, Is.EqualTo(9));
    }

    [Test]
    public async Task SlangLinterRunsCombinedSlangAndIrLintAndHandlesSubsetAndNonZeroExit()
    {
        string rv32iAst = LoadRv32iAstJson();
        var runner = new FakeRunner((exe, args, cwd) =>
        {
            int astIdx = args.ToList().IndexOf("--ast-json");
            Assert.That(astIdx, Is.GreaterThanOrEqualTo(0));
            File.WriteAllText(args[astIdx + 1], rv32iAst);
            return new ProcessResult(0, "", "rv32i_core.sv:5:3: warning: unused parameter 'P' [-Wunused-parameter]\n");
        });

        var linter = new SlangLinter(runner);
        var report = await linter.LintSystemVerilogAsync(["examples/rv32i_core.sv"], LintOptions.All, top: "rv32i_core", slangExecutable: "/fake/slang");
        Assert.That(report.Passed, Is.True);
        Assert.That(report.Diagnostics.Any(d => d.Source == "slang" && d.Code == "UNUSEDPARAM"), Is.True);
        Assert.That(runner.Invocations.Count, Is.EqualTo(1));
        Assert.That(runner.Invocations[0].Args, Contains.Item("-Weverything"));

        // Unsupported SV construct in AST falls back gracefully to VS-SUBSET info instead of failing lint
        var subsetRunner = new FakeRunner((exe, args, cwd) =>
        {
            int astIdx = args.ToList().IndexOf("--ast-json");
            File.WriteAllText(args[astIdx + 1], "{\"design\":{\"kind\":\"Root\",\"members\":[]}}");
            return new ProcessResult(0, "", "");
        });
        var subsetReport = await new SlangLinter(subsetRunner).LintSystemVerilogAsync(["uvm_tb.sv"], LintOptions.All, top: "tb", slangExecutable: "/fake/slang");
        Assert.That(subsetReport.Passed, Is.True);
        Assert.That(subsetReport.Diagnostics.Select(d => d.Code), Contains.Item("VS-SUBSET"));

        // Non-zero exit without formatted diagnostic lines still emits SLANGERROR
        var failRunner = new FakeRunner((exe, args, cwd) => new ProcessResult(2, "", "fatal: unknown option"));
        var failReport = await new SlangLinter(failRunner).LintSystemVerilogAsync(["bad.sv"], LintOptions.All, slangExecutable: "/fake/slang");
        Assert.That(failReport.Passed, Is.False);
        Assert.That(failReport.Diagnostics.Select(d => d.Code), Contains.Item("SLANGERROR"));
    }

    [Test]
    public async Task ToolCliLintSupportsHelpJsonIrAstJsonSlangAndVerilatorFlags()
    {
        // Help
        var helpOut = new StringWriter();
        var helpErr = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["lint", "--help"], helpOut, helpErr), Is.Zero);
        Assert.That(helpOut.ToString(), Does.Contain("--Wall"));
        Assert.That(helpOut.ToString(), Does.Contain("--Werror"));
        Assert.That(helpErr.ToString(), Is.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "velosim-lint-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1. IR JSON with width truncation: -Wall -Werror fails; -Wno-fatal passes; --json & --output work
            string irPath = Path.Combine(dir, "trunc.rtl.json");
            await File.WriteAllTextAsync(irPath, """
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

            string reportOutPath = Path.Combine(dir, "reports", "lint.json");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int rcFail = await ToolCli.RunAsync(["lint", irPath, "-Wall", "-Werror", "--json", "--output", reportOutPath], stdout, stderr);
            Assert.That(rcFail, Is.EqualTo(1));
            Assert.That(File.Exists(reportOutPath), Is.True);
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportOutPath)))
            {
                Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("fail"));
                Assert.That(doc.RootElement.GetProperty("error_count").GetInt32(), Is.EqualTo(1));
                Assert.That(doc.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString(), Is.EqualTo("WIDTHTRUNC"));
            }

            // Same design with -Wno-WIDTHTRUNC passes even under -Wall -Werror
            stdout = new StringWriter();
            stderr = new StringWriter();
            int rcSuppressed = await ToolCli.RunAsync(["--lint-only", irPath, "-Wall", "-Werror", "-Wno-WIDTHTRUNC"], stdout, stderr);
            Assert.That(rcSuppressed, Is.Zero, stdout.ToString() + stderr.ToString());
            Assert.That(stdout.ToString(), Does.Contain("lint pass: 0 error(s), 0 warning(s)"));

            // -Werror-WIDTHTRUNC promotes single rule to error
            stdout = new StringWriter();
            stderr = new StringWriter();
            int rcSingleErr = await ToolCli.RunAsync(["lint", irPath, "-Werror-WIDTHTRUNC", "--large-design"], stdout, stderr);
            Assert.That(rcSingleErr, Is.EqualTo(1));
            Assert.That(stdout.ToString(), Does.Contain("%Error-WIDTHTRUNC:"));

            // 2. --ast-json direct lint (no slang binary needed)
            string astPath = Path.Combine(dir, "rv32i.ast.json");
            await File.WriteAllTextAsync(astPath, LoadRv32iAstJson());
            stdout = new StringWriter();
            stderr = new StringWriter();
            int rcAst = await ToolCli.RunAsync(["lint", "--ast-json", astPath, "--top", "rv32i_core", "-Wall", "-Werror"], stdout, stderr);
            Assert.That(rcAst, Is.Zero, stdout.ToString() + stderr.ToString());

            // 3. .sv file via mock IProcessRunner with -I, -D, -W, --slang-only
            string svPath = Path.Combine(dir, "demo.sv");
            await File.WriteAllTextAsync(svPath, "module demo; endmodule\n");
            var mockRunner = new FakeRunner((exe, args, cwd) =>
                new ProcessResult(0, "", $"{svPath}:1:1: warning: unused port 'x' [-Wunused-port]\n"));
            stdout = new StringWriter();
            stderr = new StringWriter();
            int rcSv = await ToolCli.RunAsync(["lint", svPath, "--lint-only", "--slang-only", "-Wall", "-Werror", "-Wno-fatal", "-I" + dir, "-DSYN=1", "-WIMPLICIT"], stdout, stderr, mockRunner);
            Assert.That(rcSv, Is.Zero, stdout.ToString() + stderr.ToString());
            Assert.That(stdout.ToString(), Does.Contain("%Warning-UNUSEDSIGNAL:"));
            Assert.That(mockRunner.Invocations[0].Args, Contains.Item("--lint-only"));
            Assert.That(mockRunner.Invocations[0].Args, Contains.Item("-Weverything"));
            Assert.That(mockRunner.Invocations[0].Args, Contains.Item("-D"));
            Assert.That(mockRunner.Invocations[0].Args, Contains.Item("SYN=1"));

            // 4. Error handling: missing source, unknown option, .uhdm without --top
            stdout = new StringWriter();
            stderr = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["lint", "--bogus-flag", irPath], stdout, stderr), Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("Unknown lint option"));

            stdout = new StringWriter();
            stderr = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["lint"], stdout, stderr), Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("Usage: lint"));

            stdout = new StringWriter();
            stderr = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["lint", Path.Combine(dir, "design.uhdm")], stdout, stderr), Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("--top"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
