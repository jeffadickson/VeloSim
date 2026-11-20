using SchematicLab.WebHost;
using SchematicLab.Solver;
using Xunit;

/// <summary>Deployment and malformed CLI contracts independent of the source checkout.</summary>
public sealed class StandaloneStartupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "schematic-startup-" + Guid.NewGuid().ToString("N"));

    public StandaloneStartupTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "standalone-fixture");
    }

    [Fact]
    public void DefaultsResolveFromDeploymentAndKeepNativeSolverOptional()
    {
        var options = Served(StartupOptions.Parse([], root));
        Assert.Equal(Path.Combine(root, "wwwroot"), options.WebRoot);
        Assert.Equal(5087, options.Port);
        Assert.Null(options.SolverPath);
    }

    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("5088", 5088)]
    [InlineData("65535", 65535)]
    public void ExplicitPortRetainsInclusiveBounds(string text, int expected) =>
        Assert.Equal(expected, Served(StartupOptions.Parse(["--port", text], root)).Port);

    [Theory]
    [InlineData("0")]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+5088")]
    [InlineData(" 5088")]
    [InlineData("1e4")]
    [InlineData("1.5")]
    [InlineData("999999999999999999999999999")]
    public void InvalidPortRefusesBeforeHosting(string text) =>
        Assert.Equal("invalid_port", Error(StartupOptions.Parse(["--port", text], root)).Code);

    [Fact]
    public void HelpDoesNotRequireAssetsSolverOrAValidApplicationDirectory() =>
        Assert.True(StartupOptions.Parse(["--help"], "relative") switch { ShowSchematicHelp => true, _ => false });

    [Fact]
    public void DuplicateUnknownMissingAndOversizedArgumentsRefuse()
    {
        Assert.Equal("duplicate_option", Error(StartupOptions.Parse(["--port", "5088", "--port", "5088"], root)).Code);
        Assert.Equal("unknown_option", Error(StartupOptions.Parse(["--urls", "http://0.0.0.0"], root)).Code);
        Assert.Equal("missing_value", Error(StartupOptions.Parse(["--web-root"], root)).Code);
        Assert.Equal("argument_budget", Error(StartupOptions.Parse(Enumerable.Repeat("x", 65).ToArray(), root)).Code);
        Assert.Equal("argument_budget", Error(StartupOptions.Parse([new string('x', 4097)], root)).Code);
        Assert.Equal("invalid_arguments", Error(StartupOptions.Parse(null, root)).Code);
        Assert.Equal("invalid_arguments", Error(StartupOptions.Parse([null], root)).Code);
    }

    [Fact]
    public void ExplicitAbsolutePathsOverrideEnvironmentAndMustExist()
    {
        var solver = Path.Combine(root, OperatingSystem.IsWindows() ? "fixture.exe" : "fixture");
        File.WriteAllText(solver, "not-executed-by-parser");
        var options = Served(StartupOptions.Parse(["--web-root", Path.Combine(root, "wwwroot"), "--solver", solver], root, "invalid", "invalid"));
        Assert.Equal(solver, options.SolverPath);
        Assert.Equal("invalid_web_root", Error(StartupOptions.Parse(["--web-root", "relative"], root)).Code);
        Assert.Equal("invalid_solver", Error(StartupOptions.Parse(["--solver", Path.Combine(root, "missing")], root)).Code);
        Assert.Equal("invalid_solver", Error(StartupOptions.Parse([], root, null, "relative")).Code);
    }

    [Theory]
    [InlineData("--web-root", "", "invalid_web_root")]
    [InlineData("--solver", " ", "invalid_solver")]
    public void EmptyExplicitPathsDoNotSilentlyUseDefaults(string option, string value, string code) =>
        Assert.Equal(code, Error(StartupOptions.Parse([option, value], root)).Code);

    [Fact]
    public void PackagedSolverIsSelectedWithoutEnvironmentConfiguration()
    {
        var directory = Path.Combine(root, "solver");
        Directory.CreateDirectory(directory);
        var solver = Path.Combine(directory, OperatingSystem.IsWindows() ? "circuit-simulator.exe" : "circuit-simulator");
        File.WriteAllText(solver, "not-executed-by-parser");
        Assert.Equal(solver, Served(StartupOptions.Parse([], root)).SolverPath);
    }

    [Fact]
    public void McpParentLookupRequiresTheMarkedBundleAndTheMcpDirectory()
    {
        var solverDirectory = Path.Combine(root, "solver");
        var mcp = Path.Combine(root, "mcp");
        Directory.CreateDirectory(solverDirectory);
        Directory.CreateDirectory(mcp);
        var solver = Path.Combine(solverDirectory, OperatingSystem.IsWindows() ? "circuit-simulator.exe" : "circuit-simulator");
        File.WriteAllText(solver, "not-executed-by-parser");
        Assert.Null(SolverLocation.FindPackaged(mcp));
        File.WriteAllText(Path.Combine(root, "schematic-bundle.json"), "marker-fixture");
        Assert.Equal(solver, SolverLocation.FindPackaged(mcp));
        Assert.Null(SolverLocation.FindPackaged(Path.Combine(root, "unrelated")));
        Assert.Throws<ArgumentException>(() => SolverLocation.FindPackaged("relative"));
    }

    [Fact]
    public void MissingBrowserEntryPointIsAnExplicitStartupFailure()
    {
        File.Delete(Path.Combine(root, "wwwroot", "index.html"));
        Assert.Equal("invalid_web_root", Error(StartupOptions.Parse([], root)).Code);
    }

    [FsCheck.Xunit.Property(MaxTest = 128, Replay = "110011,2026111201")]
    public bool ArbitraryArgumentVectorsRemainTypedAndDeterministic(string[] arguments)
    {
        var first = StartupOptions.Parse(arguments, root);
        var second = StartupOptions.Parse(arguments, root);
        return first.Equals(second) && (first switch
        {
            ServeSchematic serve => serve.Options.Port is >= 1024 and <= 65535 && Path.IsPathFullyQualified(serve.Options.WebRoot),
            InvalidSchematicStartup error => !string.IsNullOrWhiteSpace(error.Code) && !string.IsNullOrWhiteSpace(error.Message),
            ShowSchematicHelp => arguments is ["--help"] or ["-h"],
        });
    }

    private static SchematicHostOptions Served(StartupCommand command) => command switch
    {
        ServeSchematic serve => serve.Options,
        InvalidSchematicStartup error => throw new Xunit.Sdk.XunitException(error.Message),
        ShowSchematicHelp => throw new Xunit.Sdk.XunitException("Unexpected help"),
    };

    private static InvalidSchematicStartup Error(StartupCommand command) => command switch
    {
        InvalidSchematicStartup error => error,
        ServeSchematic => throw new Xunit.Sdk.XunitException("Invalid input started a server"),
        ShowSchematicHelp => throw new Xunit.Sdk.XunitException("Invalid input became help"),
    };

    public void Dispose()
    {
        // Only remove this fixture's resolved, uniquely named temporary subtree.
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup escaped temporary root.");
        Directory.Delete(root, true);
    }
}
