using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class Net11AndDafnyAuditTests
{
    private static readonly string[] RequiredDafnyModules =
    [
        "formal/dafny/FourStateResolution.dfy",
        "formal/dafny/DagLevelizer.dfy",
        "formal/dafny/SvaDelayPipeline.dfy",
        "formal/dafny/CoverageMergeInvariants.dfy"
    ];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VeloSim.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate VeloSim repository root.");
    }

    [Test]
    public void CoverBinSpec_CSharp15Union_ExhaustivelyEncodesAllMutuallyExclusiveVariants()
    {
        var ir = """
        {
          "format": "velosim.rtl.v1",
          "clock": "clk",
          "signals": [
            { "name": "clk", "width": 1, "direction": "input" },
            { "name": "x", "width": 4, "direction": "input" },
            { "name": "y", "width": 4, "direction": "output" }
          ],
          "memories": [],
          "continuous": [],
          "covergroups": [
            {
              "name": "cg",
              "coverpoints": [
                {
                  "name": "cp_x",
                  "value": { "kind": "ref", "name": "x" },
                  "bins": [
                    { "name": "low", "lo": 0, "hi": 3 },
                    { "name": "ign", "value": 14, "ignore": true },
                    { "name": "bad", "value": 15, "illegal": true },
                    { "name": "other", "default": true }
                  ]
                }
              ]
            }
          ],
          "body": {
            "kind": "process",
            "events": [{ "signal": "clk", "edge": "posedge" }],
            "body": {
              "kind": "block",
              "items": [
                { "kind": "nba", "target": { "kind": "ref", "name": "y" }, "value": { "kind": "ref", "name": "x" } },
                { "kind": "sample_covergroup", "name": "cg" }
              ]
            }
          }
        }
        """;

        var design = RtlDesign.Parse(ir);
        var bins = design.Covergroups[0].Points[0].Bins;
        Assert.That(bins.Count, Is.EqualTo(4));

        static string Describe(RtlCoverBinSpec spec) => spec switch
        {
            RtlValueRangeBin r => $"range:{r.Lo}..{r.Hi}",
            RtlIgnoreBin ig => $"ignore:{ig.Lo}..{ig.Hi}",
            RtlIllegalBin il => $"illegal:{il.Lo}..{il.Hi}",
            RtlDefaultBin => "default"
        };

        Assert.That(Describe(bins[0].Spec), Is.EqualTo("range:0..3"));
        Assert.That(bins[0].IsDefault || bins[0].IsIgnore || bins[0].IsIllegal, Is.False);

        Assert.That(Describe(bins[1].Spec), Is.EqualTo("ignore:14..14"));
        Assert.That(bins[1].IsIgnore, Is.True);
        Assert.That(bins[1].IsDefault || bins[1].IsIllegal, Is.False);

        Assert.That(Describe(bins[2].Spec), Is.EqualTo("illegal:15..15"));
        Assert.That(bins[2].IsIllegal, Is.True);
        Assert.That(bins[2].IsDefault || bins[2].IsIgnore, Is.False);

        Assert.That(Describe(bins[3].Spec), Is.EqualTo("default"));
        Assert.That(bins[3].IsDefault, Is.True);
        Assert.That(bins[3].Lo, Is.EqualTo(0UL));
        Assert.That(bins[3].Hi, Is.EqualTo(0UL));
    }

    [Test]
    public async Task ExportedCSharpSdk_PinsNet11CSharp15AndBuildsZeroWarningsUnderWarnAsError()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "velosim-net11-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            string irPath = Path.Combine(tmp, "design.rtl.json");
            string sdkDir = Path.Combine(tmp, "sdk");
            string ir = """
            {
              "format": "velosim.rtl.v1",
              "clock": "clk",
              "signals": [
                { "name": "clk", "width": 1, "direction": "input" },
                { "name": "a", "width": 8, "direction": "input" },
                { "name": "b", "width": 8, "direction": "input" },
                { "name": "sum", "width": 8, "direction": "output" }
              ],
              "memories": [],
              "continuous": [],
              "body": {
                "kind": "process",
                "events": [{ "signal": "clk", "edge": "posedge" }],
                "body": {
                  "kind": "nba",
                  "target": { "kind": "ref", "name": "sum" },
                  "value": {
                    "kind": "add",
                    "args": [
                      { "kind": "ref", "name": "a" },
                      { "kind": "ref", "name": "b" }
                    ]
                  }
                }
              }
            }
            """;
            await File.WriteAllTextAsync(irPath, ir);
            await RtlExternalSdk.ExportAsync(irPath, "net11_dut", sdkDir, target: RtlTargetLanguage.CSharp);

            string csprojPath = Path.Combine(sdkDir, "Vnet11_dut.csproj");
            string csproj = await File.ReadAllTextAsync(csprojPath);
            Assert.That(csproj, Does.Contain("<TargetFramework>net11.0</TargetFramework>"));
            Assert.That(csproj, Does.Contain("<LangVersion>15.0</LangVersion>"));
            Assert.That(csproj, Does.Contain("<AnalysisLevel>11.0</AnalysisLevel>"));
            Assert.That(csproj, Does.Contain("<Deterministic>true</Deterministic>"));

            var psi = new ProcessStartInfo("dotnet", $"build \"{csprojPath}\" -c Release --nologo -p:TreatWarningsAsErrors=true")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            Assert.That(proc.ExitCode, Is.EqualTo(0), $"Exported C# SDK failed -warnaserror build:\n{stdout}\n{stderr}");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public void DafnyFormalSuite_AllModulesExistAndContainNoAxiomOrAssumeBypasses()
    {
        string root = RepoRoot();
        var bypassRegex = new Regex(@"\bassume\b|\{:axiom\}", RegexOptions.Compiled);
        foreach (string relPath in RequiredDafnyModules)
        {
            string fullPath = Path.Combine(root, relPath);
            Assert.That(File.Exists(fullPath), Is.True, $"Missing required Dafny formal module: {relPath}");
            string codeOnly = string.Join('\n', File.ReadAllLines(fullPath).Select(l =>
            {
                int idx = l.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? l[..idx] : l;
            }));
            Assert.That(bypassRegex.IsMatch(codeOnly), Is.False, $"Dafny formal module {relPath} must not contain assume or {{:axiom}} bypasses.");
        }
        Assert.That(File.Exists(Path.Combine(root, "docs/dafny-verification.md")), Is.True);
    }
}
