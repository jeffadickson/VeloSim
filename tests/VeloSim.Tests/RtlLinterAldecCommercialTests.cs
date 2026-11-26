using System.IO.Compression;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlLinterAldecCommercialTests
{
    [Test]
    public void FsmAnalyzerExtractsCleanTwoProcessFsmWithMermaidAndJsonDump()
    {
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "go", "width": 1, "direction": "input"},
                {"name": "state", "width": 2, "direction": "output"},
                {"name": "next_state", "width": 2, "direction": "internal"}
              ],
              "memories": [],
              "continuous": [],
              "combinational": [
                {
                  "kind": "case",
                  "value": {"kind": "ref", "name": "state"},
                  "items": [
                    {
                      "match": {"kind": "const", "bits": "00"},
                      "body": {
                        "kind": "if",
                        "condition": {"kind": "ref", "name": "go"},
                        "then": {"kind": "blocking", "target": {"kind": "ref", "name": "next_state"}, "value": {"kind": "const", "bits": "01"}},
                        "else": {"kind": "blocking", "target": {"kind": "ref", "name": "next_state"}, "value": {"kind": "const", "bits": "00"}}
                      }
                    },
                    {
                      "match": {"kind": "const", "bits": "01"},
                      "body": {"kind": "blocking", "target": {"kind": "ref", "name": "next_state"}, "value": {"kind": "const", "bits": "10"}}
                    },
                    {
                      "match": {"kind": "const", "bits": "10"},
                      "body": {"kind": "blocking", "target": {"kind": "ref", "name": "next_state"}, "value": {"kind": "const", "bits": "00"}}
                    },
                    {
                      "match": null,
                      "body": {"kind": "blocking", "target": {"kind": "ref", "name": "next_state"}, "value": {"kind": "const", "bits": "00"}}
                    }
                  ]
                }
              ],
              "clock": "clk",
              "body": {
                "kind": "process",
                "events": [{"signal": "clk", "edge": "posedge"}],
                "body": {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "rst"},
                  "then": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}},
                  "else": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "ref", "name": "next_state"}}
                }
              }
            }
            """;

        var report = RtlLinter.LintJson(ir, new LintOptions(Profile: LintProfile.Do254));
        Assert.That(report.ErrorCount, Is.EqualTo(0), report.FormatText());
        Assert.That(report.WarningCount, Is.EqualTo(0), report.FormatText());
        Assert.That(report.Fsms, Is.Not.Null.And.Count.EqualTo(1));

        var fsm = report.Fsms![0];
        Assert.That(fsm.StateSignal, Is.EqualTo("state"));
        Assert.That(fsm.NextStateSignal, Is.EqualTo("next_state"));
        Assert.That(fsm.ResetState, Is.EqualTo("00"));
        Assert.That(fsm.HasDefaultRecovery, Is.True);
        Assert.That(fsm.UnreachableStates, Is.Empty);
        Assert.That(fsm.DeadlockStates, Is.Empty);
        Assert.That(fsm.States, Is.SupersetOf(new[] { "00", "01", "10" }));

        string mermaid = fsm.ToMermaid();
        Assert.That(mermaid, Does.Contain("stateDiagram-v2"));
        Assert.That(mermaid, Does.Contain("[*] --> S_00"));
        Assert.That(mermaid, Does.Contain("S_00 --> S_01"));
        Assert.That(mermaid, Does.Contain("S_01 --> S_10"));
        Assert.That(mermaid, Does.Contain("S_10 --> S_00"));

        using var dump = JsonDocument.Parse(report.FormatFsmDumpJson());
        Assert.That(dump.RootElement.GetProperty("schema").GetString(), Is.EqualTo("velosim.lint.fsm.v1"));
        Assert.That(dump.RootElement.GetProperty("fsms").GetArrayLength(), Is.EqualTo(1));
        Assert.That(dump.RootElement.GetProperty("fsms")[0].GetProperty("mermaid").GetString(), Does.Contain("stateDiagram-v2"));
    }

    [Test]
    public void FsmAnalyzerDetectsUnreachableDeadlockMissingRecoveryAndMissingReset()
    {
        // 1-process FSM with reset=00: 00->01, 01->01 (deadlock trap!), 10->00 (unreachable!), no default recovery
        string buggyFsmIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "state", "width": 2, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "clock": "clk",
              "body": {
                "kind": "process",
                "events": [{"signal": "clk", "edge": "posedge"}],
                "body": {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "rst"},
                  "then": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}},
                  "else": {
                    "kind": "case",
                    "value": {"kind": "ref", "name": "state"},
                    "items": [
                      {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                      {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                      {"match": {"kind": "const", "bits": "10"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}}
                    ]
                  }
                }
              }
            }
            """;

        var report = RtlLinter.LintJson(buggyFsmIr, new LintOptions(Profile: LintProfile.Do254));
        var byCode = report.Diagnostics.ToLookup(d => d.Code);
        Assert.That(byCode.Contains("FSMUNREACH"), Is.True, report.FormatText());
        Assert.That(byCode.Contains("FSMDEADLOCK"), Is.True, report.FormatText());
        Assert.That(byCode.Contains("FSMRECOVERY"), Is.True, report.FormatText());
        Assert.That(byCode["FSMUNREACH"].First().Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(byCode["FSMDEADLOCK"].First().Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(byCode["FSMRECOVERY"].First().Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(report.Fsms![0].UnreachableStates, Contains.Item("10"));
        Assert.That(report.Fsms![0].DeadlockStates, Contains.Item("01"));

        // 1-process FSM with NO reset branch -> FSMNORESET
        string noResetFsmIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "state", "width": 2, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "clock": "clk",
              "body": {
                "kind": "process",
                "events": [{"signal": "clk", "edge": "posedge"}],
                "body": {
                  "kind": "case",
                  "value": {"kind": "ref", "name": "state"},
                  "items": [
                    {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                    {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}},
                    {"match": null, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}}
                  ]
                }
              }
            }
            """;

        var noResetReport = RtlLinter.LintJson(noResetFsmIr, new LintOptions(Wall: true));
        Assert.That(noResetReport.Diagnostics.Any(d => d.Code == "FSMNORESET"), Is.True, noResetReport.FormatText());
    }

    [Test]
    public void CdcAnalyzerRecognizesTwoFfSynchronizerAndFlagsUnsyncMultiBitAndCombCone()
    {
        string cdcIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk_a", "width": 1, "direction": "input"},
                {"name": "clk_b", "width": 1, "direction": "input"},
                {"name": "in_req", "width": 1, "direction": "input"},
                {"name": "in_bus", "width": 8, "direction": "input"},
                {"name": "req_a", "width": 1, "direction": "internal"},
                {"name": "unsync_a", "width": 1, "direction": "internal"},
                {"name": "bus_a", "width": 8, "direction": "internal"},
                {"name": "comb_a", "width": 1, "direction": "internal"},
                {"name": "req_sync1", "width": 1, "direction": "internal"},
                {"name": "req_sync2", "width": 1, "direction": "output"},
                {"name": "unsync_b", "width": 1, "direction": "output"},
                {"name": "bus_b", "width": 8, "direction": "output"},
                {"name": "comb_b", "width": 1, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "comb_a"}, "value": {"kind": "xor", "args": [{"kind": "ref", "name": "req_a"}, {"kind": "ref", "name": "unsync_a"}]}}
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
                        {"kind": "nba", "target": {"kind": "ref", "name": "req_a"}, "value": {"kind": "ref", "name": "in_req"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "unsync_a"}, "value": {"kind": "ref", "name": "in_req"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "bus_a"}, "value": {"kind": "ref", "name": "in_bus"}}
                      ]
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_b", "edge": "posedge"}],
                    "body": {
                      "kind": "block",
                      "items": [
                        {"kind": "nba", "target": {"kind": "ref", "name": "req_sync1"}, "value": {"kind": "ref", "name": "req_a"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "req_sync2"}, "value": {"kind": "ref", "name": "req_sync1"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "unsync_b"}, "value": {"kind": "ref", "name": "unsync_a"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "bus_b"}, "value": {"kind": "ref", "name": "bus_a"}},
                        {"kind": "nba", "target": {"kind": "ref", "name": "comb_b"}, "value": {"kind": "ref", "name": "comb_a"}}
                      ]
                    }
                  }
                ]
              }
            }
            """;

        var report = RtlLinter.LintJson(cdcIr, LintOptions.All);
        Assert.That(report.Cdc, Is.Not.Null);
        Assert.That(report.Cdc!.ClockDomains.Select(d => d.ClockSignal), Is.EquivalentTo(new[] { "clk_a", "clk_b" }));

        var syncCrossing = report.Cdc.Crossings.Single(c => c.SourceSignal == "req_a" && c.DestinationSignal == "req_sync1");
        Assert.That(syncCrossing.Synchronized, Is.True);
        Assert.That(syncCrossing.SynchronizerStages, Is.EqualTo(2));

        var cdcDiags = report.Diagnostics.Where(d => d.Code == "CDCUNSYNC").ToList();
        Assert.That(cdcDiags.Any(d => d.Location?.Signal == "req_sync1"), Is.False, "2-FF synchronizer should not trigger CDCUNSYNC");
        Assert.That(cdcDiags.Any(d => d.Location?.Signal == "unsync_b"), Is.True, report.FormatText());
        Assert.That(cdcDiags.Any(d => d.Location?.Signal == "bus_b"), Is.True, report.FormatText());
        Assert.That(cdcDiags.Any(d => d.Location?.Signal == "comb_b"), Is.True, report.FormatText());

        string sva = report.Cdc.GenerateSvaAssertions("dual_clk");
        Assert.That(sva, Does.Contain("module dual_clk_chk;"));
        Assert.That(sva, Does.Contain("assert property"));
        Assert.That(sva, Does.Contain("@(posedge clk_b)"));

        using var dump = JsonDocument.Parse(report.FormatCdcDumpJson());
        Assert.That(dump.RootElement.GetProperty("schema").GetString(), Is.EqualTo("velosim.lint.cdc.v1"));
        Assert.That(dump.RootElement.GetProperty("clock_domains").GetArrayLength(), Is.EqualTo(2));
        Assert.That(dump.RootElement.GetProperty("crossings").GetArrayLength(), Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void CdcAnalyzerDetectsRdcDftControllabilityAndAppliesSdcConstraints()
    {
        // Design with:
        // - clk_a (primary), clk_gen (internally generated -> DFTCLKCTRL), gen_rst (internally generated async reset -> DFTRSTCTRL)
        // - rst_a & rst_b reset domains on clk_a -> RDCUNSYNC from r_a to r_b
        // - CDC crossing from sig_a (clk_a) to sig_gen (clk_gen) and to sig_c (clk_c)
        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk_a", "width": 1, "direction": "input"},
                {"name": "clk_c", "width": 1, "direction": "input"},
                {"name": "rst_a", "width": 1, "direction": "input"},
                {"name": "rst_b", "width": 1, "direction": "input"},
                {"name": "din", "width": 1, "direction": "input"},
                {"name": "clk_gen", "width": 1, "direction": "internal"},
                {"name": "gen_rst", "width": 1, "direction": "internal"},
                {"name": "r_a", "width": 1, "direction": "internal"},
                {"name": "r_b", "width": 1, "direction": "output"},
                {"name": "sig_gen", "width": 1, "direction": "output"},
                {"name": "sig_c", "width": 1, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "gen_rst"}, "value": {"kind": "and", "args": [{"kind": "ref", "name": "rst_a"}, {"kind": "ref", "name": "din"}]}}
              ],
              "clock": "clk_a",
              "body": {
                "kind": "parallel",
                "items": [
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_a", "edge": "posedge"}, {"signal": "rst_a", "edge": "posedge"}],
                    "body": {
                      "kind": "if",
                      "condition": {"kind": "ref", "name": "rst_a"},
                      "then": {
                        "kind": "block",
                        "items": [
                          {"kind": "nba", "target": {"kind": "ref", "name": "r_a"}, "value": {"kind": "const", "bits": "0"}},
                          {"kind": "nba", "target": {"kind": "ref", "name": "clk_gen"}, "value": {"kind": "const", "bits": "0"}}
                        ]
                      },
                      "else": {
                        "kind": "block",
                        "items": [
                          {"kind": "nba", "target": {"kind": "ref", "name": "r_a"}, "value": {"kind": "ref", "name": "din"}},
                          {"kind": "nba", "target": {"kind": "ref", "name": "clk_gen"}, "value": {"kind": "not", "args": [{"kind": "ref", "name": "clk_gen"}]}}
                        ]
                      }
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_a", "edge": "posedge"}, {"signal": "rst_b", "edge": "posedge"}],
                    "body": {
                      "kind": "if",
                      "condition": {"kind": "ref", "name": "rst_b"},
                      "then": {"kind": "nba", "target": {"kind": "ref", "name": "r_b"}, "value": {"kind": "const", "bits": "0"}},
                      "else": {"kind": "nba", "target": {"kind": "ref", "name": "r_b"}, "value": {"kind": "ref", "name": "r_a"}}
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_gen", "edge": "posedge"}, {"signal": "gen_rst", "edge": "posedge"}],
                    "body": {
                      "kind": "if",
                      "condition": {"kind": "ref", "name": "gen_rst"},
                      "then": {"kind": "nba", "target": {"kind": "ref", "name": "sig_gen"}, "value": {"kind": "const", "bits": "0"}},
                      "else": {"kind": "nba", "target": {"kind": "ref", "name": "sig_gen"}, "value": {"kind": "ref", "name": "r_a"}}
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_c", "edge": "posedge"}],
                    "body": {"kind": "nba", "target": {"kind": "ref", "name": "sig_c"}, "value": {"kind": "ref", "name": "r_a"}}
                  }
                ]
              }
            }
            """;

        // SDC declares:
        // - clk_a (primary)
        // - clk_gen generated from clk_a (synchronous -> suppresses CDCUNSYNC on sig_gen!)
        // - clk_ghost (not in RTL -> SDCOVERCONSTR!)
        // - clk_c omitted from SDC -> SDCUCONSTR!
        // - false path from r_a to sig_c -> suppresses CDCUNSYNC on sig_c!
        string sdcText = """
            # Test SDC constraints
            create_clock -name sys_clk -period 10.0 [get_ports clk_a]
            create_generated_clock -name div_clk -source [get_ports clk_a] -divide_by 2 [get_nets clk_gen]
            create_clock -name ghost_clk -period 5.0 [get_ports clk_ghost]
            set_clock_groups -asynchronous -group {sys_clk div_clk} -group {clk_c}
            set_false_path -from [get_registers r_a] -to [get_ports sig_c]
            set_reset [get_ports rst_a]
            """;

        var sdc = SdcConstraints.Parse(sdcText);
        Assert.That(sdc.AreClocksSynchronous("clk_a", "clk_gen"), Is.True);
        Assert.That(sdc.AreClocksSynchronous("clk_a", "clk_c"), Is.False);
        Assert.That(sdc.IsFalsePath("r_a", "clk_a", "sig_c", "clk_c"), Is.True);
        Assert.That(sdc.DeclaredResets, Contains.Item("rst_a"));

        var report = RtlLinter.LintJson(ir, new LintOptions(Wall: true, Sdc: sdc, HasExplicitSdc: true));
        var codes = report.Diagnostics.Select(d => d.Code).ToHashSet();

        Assert.That(codes, Contains.Item("RDCUNSYNC"), report.FormatText());
        Assert.That(codes, Contains.Item("DFTCLKCTRL"), report.FormatText());
        Assert.That(codes, Contains.Item("DFTRSTCTRL"), report.FormatText());
        Assert.That(codes, Contains.Item("SDCUCONSTR"), report.FormatText());
        Assert.That(codes, Contains.Item("SDCOVERCONSTR"), report.FormatText());
        Assert.That(codes, Does.Not.Contain("CDCUNSYNC"),
            "Generated sync clock (clk_gen) and false-path (r_a -> sig_c) should suppress CDCUNSYNC: " + report.FormatText());
    }

    [Test]
    public void PhaseBasedLintingFiltersPhasesAndStopsOnPhaseError()
    {
        // Design with findings in ALL 4 phases:
        // - Phase 1 (rtl): ASSIGNIN (continuous drive to input 'in_a')
        // - Phase 2 (fsm): FSMDEADLOCK (state 01 loops to 01)
        // - Phase 3 (cdc): CDCUNSYNC (cross from clk_a to clk_b)
        // - Phase 4 (style): UNUSEDSIGNAL (unused_sig)
        string multiPhaseIr = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk_a", "width": 1, "direction": "input"},
                {"name": "clk_b", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "in_a", "width": 1, "direction": "input"},
                {"name": "unused_sig", "width": 4, "direction": "input"},
                {"name": "state", "width": 2, "direction": "output"},
                {"name": "a_reg", "width": 1, "direction": "internal"},
                {"name": "b_reg", "width": 1, "direction": "output"}
              ],
              "memories": [],
              "continuous": [
                {"target": {"kind": "ref", "name": "in_a"}, "value": {"kind": "const", "bits": "1"}}
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
                        {"kind": "nba", "target": {"kind": "ref", "name": "a_reg"}, "value": {"kind": "ref", "name": "in_a"}},
                        {
                          "kind": "if",
                          "condition": {"kind": "ref", "name": "rst"},
                          "then": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}},
                          "else": {
                            "kind": "case",
                            "value": {"kind": "ref", "name": "state"},
                            "items": [
                              {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                              {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                              {"match": null, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}}
                            ]
                          }
                        }
                      ]
                    }
                  },
                  {
                    "kind": "process",
                    "events": [{"signal": "clk_b", "edge": "posedge"}],
                    "body": {"kind": "nba", "target": {"kind": "ref", "name": "b_reg"}, "value": {"kind": "ref", "name": "a_reg"}}
                  }
                ]
              }
            }
            """;

        // Full run has all 4 phases
        var full = RtlLinter.LintJson(multiPhaseIr, LintOptions.All);
        Assert.That(full.Diagnostics.Select(d => d.Phase).Distinct(), Is.SupersetOf(new[] { "rtl", "fsm", "cdc", "style" }));

        // Phase filter: only FSM + CDC
        var fsmCdcOnly = RtlLinter.LintJson(multiPhaseIr, new LintOptions(Wall: true, Phases: LintPhases.ParseList("fsm,cdc")));
        Assert.That(fsmCdcOnly.Diagnostics.All(d => d.Phase is "fsm" or "cdc"), Is.True, fsmCdcOnly.FormatText());
        Assert.That(fsmCdcOnly.Diagnostics.Any(d => d.Code == "FSMDEADLOCK"), Is.True);
        Assert.That(fsmCdcOnly.Diagnostics.Any(d => d.Code == "CDCUNSYNC"), Is.True);

        // Stop on phase error: ASSIGNIN is an error in phase 'rtl', so fsm/cdc/style are suppressed
        var stopped = RtlLinter.LintJson(multiPhaseIr, new LintOptions(Wall: true, StopOnPhaseError: true));
        Assert.That(stopped.StoppedAtPhase, Is.EqualTo("rtl"));
        Assert.That(stopped.Diagnostics.All(d => d.Phase == "rtl"), Is.True, stopped.FormatText());
        Assert.That(stopped.FormatText(), Does.Contain("Phase-Based Linting stopped after phase 'rtl'"));
        using var jsonDoc = JsonDocument.Parse(stopped.ToJson());
        Assert.That(jsonDoc.RootElement.GetProperty("stopped_at_phase").GetString(), Is.EqualTo("rtl"));
    }

    [Test]
    public void ComplianceProfilesAnnotationsQualityScoreAndGroupedOutput()
    {
        Assert.That(LintProfiles.Parse("do-254"), Is.EqualTo(LintProfile.Do254));
        Assert.That(LintProfiles.Parse("starc"), Is.EqualTo(LintProfile.Starc));
        Assert.That(LintProfiles.Parse("iso-26262"), Is.EqualTo(LintProfile.Iso26262));
        Assert.That(LintProfiles.Parse("opentitan"), Is.EqualTo(LintProfile.LowRisc));

        string configJson = """
            {
              "schema": "velosim.lint.config.v1",
              "profile": "starc",
              "annotations": {
                "CDCUNSYNC": "CORP-CDC-001",
                "UNUSEDSIGNAL": "CORP-STYLE-007"
              }
            }
            """;
        var cfg = LintConfig.Parse(configJson);

        string ir = """
            {
              "format": "velosim.rtl.v1",
              "signals": [
                {"name": "clk_a", "width": 1, "direction": "input"},
                {"name": "clk_b", "width": 1, "direction": "input"},
                {"name": "d_in", "width": 1, "direction": "input"},
                {"name": "unused_x", "width": 4, "direction": "input"},
                {"name": "q_a", "width": 1, "direction": "internal"},
                {"name": "q_b", "width": 1, "direction": "output"}
              ],
              "memories": [],
              "continuous": [],
              "clock": "clk_a",
              "body": {
                "kind": "parallel",
                "items": [
                  {"kind": "process", "events": [{"signal": "clk_a", "edge": "posedge"}], "body": {"kind": "nba", "target": {"kind": "ref", "name": "q_a"}, "value": {"kind": "ref", "name": "d_in"}}},
                  {"kind": "process", "events": [{"signal": "clk_b", "edge": "posedge"}], "body": {"kind": "nba", "target": {"kind": "ref", "name": "q_b"}, "value": {"kind": "ref", "name": "q_a"}}}
                ]
              }
            }
            """;

        var report = RtlLinter.LintJson(ir, new LintOptions(Config: cfg), sourceFile: "top.sv");
        var cdcDiag = report.Diagnostics.Single(d => d.Code == "CDCUNSYNC");
        Assert.That(cdcDiag.Severity, Is.EqualTo(LintSeverity.Error));
        Assert.That(cdcDiag.GuidelineTags, Contains.Item("CORP-CDC-001"));
        Assert.That(cdcDiag.GuidelineTags, Contains.Item("STARC-1.4.3.1"));

        // Quality score drops because of the error + warning
        Assert.That(report.EffectiveQuality.Score, Is.LessThan(90.0));
        Assert.That(report.EffectiveQuality.DesignElements, Is.GreaterThan(0));

        // Grouped text formatting
        Assert.That(report.FormatText(LintGroupBy.Rule), Does.Contain("=== [CDCUNSYNC]"));
        Assert.That(report.FormatText(LintGroupBy.Signal), Does.Contain("=== [q_b]"));
        Assert.That(report.FormatText(LintGroupBy.File), Does.Contain("=== [top.sv]"));
        Assert.That(report.FormatText(LintGroupBy.Phase), Does.Contain("=== [phase:cdc]"));

        // JSON and SARIF include guideline_tags / properties.tags / quality
        using var jsonDoc = JsonDocument.Parse(report.ToJson());
        Assert.That(jsonDoc.RootElement.GetProperty("quality").GetProperty("grade").GetString(), Is.Not.Empty);
        var jsonCdc = jsonDoc.RootElement.GetProperty("diagnostics").EnumerateArray().Single(d => d.GetProperty("code").GetString() == "CDCUNSYNC");
        Assert.That(jsonCdc.GetProperty("guideline_tags").EnumerateArray().Select(e => e.GetString()), Contains.Item("CORP-CDC-001"));

        using var sarifDoc = JsonDocument.Parse(report.ToSarif());
        var rules = sarifDoc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var sarifCdcRule = rules.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "CDCUNSYNC");
        Assert.That(sarifCdcRule.GetProperty("properties").GetProperty("phase").GetString(), Is.EqualTo("cdc"));
        Assert.That(sarifCdcRule.GetProperty("properties").GetProperty("tags").EnumerateArray().Select(e => e.GetString()), Contains.Item("DO-254"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "VeloSim.slnx")))
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("Could not locate VeloSim.slnx");
        return dir;
    }

    [Test]
    public void Rv32iCorePassesDo254StarcIso26262AndLowRiscComplianceProfilesCleanly()
    {
        string fixturePath = Path.Combine(RepoRoot(), "tests", "VeloSim.Tests", "Fixtures", "rv32i_core.slang.ast.json.gz");
        using var fs = File.OpenRead(fixturePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        string astJson = sr.ReadToEnd();
        string irJson = SlangAstLowerer.LowerAstJson(astJson, "rv32i_core");

        foreach (var profile in new[] { LintProfile.Do254, LintProfile.Starc, LintProfile.Iso26262, LintProfile.LowRisc })
        {
            var report = RtlLinter.LintJson(irJson, new LintOptions(Profile: profile), "examples/rv32i_core.sv");
            Assert.That(report.ErrorCount, Is.EqualTo(0), $"Profile {profile} errors:\n{report.FormatText()}");
            Assert.That(report.WarningCount, Is.EqualTo(0), $"Profile {profile} warnings:\n{report.FormatText()}");
            Assert.That(report.EffectiveQuality.Score, Is.EqualTo(100.0));
            Assert.That(report.EffectiveQuality.Grade, Is.EqualTo("A+"));
        }
    }

    [Test]
    public async Task ToolCliSupportsPhaseSdcGroupByDumpFsmDumpCdcAndEmitCdcSva()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "velosim-aldec-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string irPath = Path.Combine(tempDir, "design.rtl.json");
            string sdcPath = Path.Combine(tempDir, "constraints.sdc");
            string fsmOut = Path.Combine(tempDir, "fsm.json");
            string cdcOut = Path.Combine(tempDir, "cdc.json");
            string svaOut = Path.Combine(tempDir, "cdc_assertions.sv");

            await File.WriteAllTextAsync(irPath, """
                {
                  "format": "velosim.rtl.v1",
                  "signals": [
                    {"name": "clk_a", "width": 1, "direction": "input"},
                    {"name": "clk_b", "width": 1, "direction": "input"},
                    {"name": "rst", "width": 1, "direction": "input"},
                    {"name": "state", "width": 2, "direction": "output"},
                    {"name": "b_state", "width": 2, "direction": "output"}
                  ],
                  "memories": [],
                  "continuous": [],
                  "clock": "clk_a",
                  "body": {
                    "kind": "parallel",
                    "items": [
                      {
                        "kind": "process",
                        "events": [{"signal": "clk_a", "edge": "posedge"}],
                        "body": {
                          "kind": "if",
                          "condition": {"kind": "ref", "name": "rst"},
                          "then": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}},
                          "else": {
                            "kind": "case",
                            "value": {"kind": "ref", "name": "state"},
                            "items": [
                              {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "01"}}},
                              {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}},
                              {"match": null, "body": {"kind": "nba", "target": {"kind": "ref", "name": "state"}, "value": {"kind": "const", "bits": "00"}}}
                            ]
                          }
                        }
                      },
                      {
                        "kind": "process",
                        "events": [{"signal": "clk_b", "edge": "posedge"}],
                        "body": {"kind": "nba", "target": {"kind": "ref", "name": "b_state"}, "value": {"kind": "ref", "name": "state"}}
                      }
                    ]
                  }
                }
                """);

            await File.WriteAllTextAsync(sdcPath, """
                create_clock -name clk_a -period 10 [get_ports clk_a]
                create_clock -name clk_b -period 13 [get_ports clk_b]
                set_clock_groups -asynchronous -group {clk_a} -group {clk_b}
                """);

            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            int rc = await ToolCli.RunAsync(
                ["lint", irPath, "--no-config", "--profile", "do254", "--phase", "rtl,fsm,cdc,style", "--sdc", sdcPath, "--group-by", "phase", "--dump-fsm", fsmOut, "--dump-cdc", cdcOut, "--emit-cdc-sva", svaOut, "--top", "bridge"],
                outWriter, errWriter);

            Assert.That(rc, Is.EqualTo(1), "DO-254 promotes multi-bit CDCUNSYNC to error");
            Assert.That(outWriter.ToString(), Does.Contain("=== [phase:cdc]"));
            Assert.That(outWriter.ToString(), Does.Contain("CDCUNSYNC"));
            Assert.That(File.Exists(fsmOut), Is.True);
            Assert.That(File.Exists(cdcOut), Is.True);
            Assert.That(File.Exists(svaOut), Is.True);
            Assert.That(await File.ReadAllTextAsync(fsmOut), Does.Contain("stateDiagram-v2"));
            Assert.That(await File.ReadAllTextAsync(cdcOut), Does.Contain("clk_b"));
            Assert.That(await File.ReadAllTextAsync(svaOut), Does.Contain("module bridge_chk;"));

            // Also verify --list-rules --json exposes all 45 rules with phase and standards
            using var rulesOut = new StringWriter();
            using var rulesErr = new StringWriter();
            int listRc = await ToolCli.RunAsync(["lint", "--list-rules", "--json"], rulesOut, rulesErr);
            Assert.That(listRc, Is.EqualTo(0));
            using var rulesDoc = JsonDocument.Parse(rulesOut.ToString());
            var rulesArr = rulesDoc.RootElement.GetProperty("rules");
            Assert.That(rulesArr.GetArrayLength(), Is.EqualTo(45));
            Assert.That(rulesArr.EnumerateArray().Any(r => r.GetProperty("code").GetString() == "CDCUNSYNC" && r.GetProperty("phase").GetString() == "cdc"), Is.True);
            Assert.That(rulesArr.EnumerateArray().Any(r => r.GetProperty("code").GetString() == "FSMDEADLOCK" && r.GetProperty("phase").GetString() == "fsm"), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
