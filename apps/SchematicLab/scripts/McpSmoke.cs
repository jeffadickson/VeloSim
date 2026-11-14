#:property PublishAot=false
#:package ModelContextProtocol@2.4.0
#:project ../Core/SchematicLab.Core.csproj
#:project ../Solver/SchematicLab.Solver.csproj
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using ModelContextProtocol.Client;
using SchematicLab.Core;
using SchematicLab.Solver;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the absolute path to the built MCP host DLL.");

var nativeEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH")) ||
    SolverLocation.FindPackaged(Path.GetDirectoryName(Path.GetFullPath(args[0]))!) is not null;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var token = deadline.Token;
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
options.Converters.Add(new JsonStringEnumConverter());
await using var client = await Open();
var tools = await client.ListToolsAsync(cancellationToken: token);
var expectedTools = new[] { "read_solver_backends", "set_solver_backend", "read_interaction_policy", "set_layout_locked", "read_circuit", "read_catalog", "set_value", "place_part", "connect_pins",
    "delete_part", "reset_lesson", "run_sweep", "read_run", "load_rc", "run_transient", "move_part", "rotate_part", "mirror_part", "duplicate_part", "undo_edit", "redo_edit", "check_circuit", "set_net_label", "set_wire_route", "delete_wire", "join_routes", "delete_junction", "save_document", "load_document", "highlight_net", "read_view", "read_analysis_history", "load_bridge_demo", "load_model_demo", "load_bjt_demo", "load_bsim3_demo", "load_soi_demo", "read_analysis_fields", "apply_analysis_fields", "read_named_models", "define_named_model", "apply_named_model", "remove_named_model", "load_hierarchy_demo", "read_child_sheet", "attach_child_sheet", "run_circuit" };
Require(tools.Select(tool => tool.Name).Order().SequenceEqual(
    expectedTools.Select(name => "schematic_" + name).Order()), "Tool allowlist mismatch.");

if (Environment.GetEnvironmentVariable("SCHEMATIC_LAYOUT_MODE") == "author-locked")
{
    var policy = (await Call(client, "read_interaction_policy")).Interaction;
    Require(policy is { LayoutLocked: true, ReaderCanUnlock: false }, "Author policy missing.");
    var original = (await Call(client, "read_circuit")).Version;
    var selection = Edit(original); selection["backend"] = "Faer";
    var selectedProvider = await Call(client, "set_solver_backend", selection);
    Require(selectedProvider.Code == CommandCode.Ok && selectedProvider.Backend == SolverBackend.Faer, "Provider selection refused.");
    original = selectedProvider.Version;
    Require((await Call(client, "reset_lesson", Edit(original))).Code == CommandCode.LayoutLocked, "Locked reset accepted.");
    var unlock = Edit(original); unlock["locked"] = false;
    Require((await Call(client, "set_layout_locked", unlock)).Code == CommandCode.LayoutLocked, "Author lock bypassed.");
    var changedRc = await Call(client, "set_value", ValueEdit(original, "R1", 2000));
    Require(changedRc.Code == CommandCode.Ok, "Locked parameter edit refused.");
    Require((await Call(client, "set_value", ValueEdit(original, "R1", 3000))).Code == CommandCode.StaleRevision, "Stale parameter accepted.");
    if (nativeEnabled)
    {
        var arguments = Edit(changedRc.Version); arguments["probeId"] = "P1";
        var waveform = (await Call(client, "run_transient", arguments)).Transient;
        Require(waveform is { Samples.Count: > 2, Input.Backend: SolverBackend.Faer }, "No real locked Faer RC waveform.");
        var end = waveform!.Samples[^1];
        var reference = waveform.Input.Volts * (1 - Math.Exp(-end.Seconds / (waveform.Input.Ohms * waveform.Input.Farads)));
        Require(Math.Abs(end.Volts - reference) < .02, "Locked RC endpoint disagrees with the analytical reference.");
        var acArguments = Edit(changedRc.Version);
        acArguments["options"] = new BridgeOptions(BridgeAnalysis.Ac, "P1", [100], Phasors: [new("V1", 1, 0)]);
        var ac = (await Call(client, "run_circuit", acArguments)).Bridge;
        Require(ac is { Backend: SolverBackend.Faer, Samples.Count: 1 }, "MCP AC did not inherit Faer.");
        var acReference = Complex.One / new Complex(1, 2 * Math.PI * 100 * waveform.Input.TimeConstant);
        Require(Complex.Abs(new Complex(ac!.Samples[0].Real, ac.Samples[0].Imaginary) - acReference) < 1e-6,
            "MCP Faer AC disagrees with the analytical RC transfer function.");
    }
    Console.WriteLine($"MCP author lock, Faer selection/inheritance, value edit, stale refusal and native RC/AC={nativeEnabled} passed.");
    return;
}

var definition = await Call(client, "define_named_model", new() { ["model"] = new NamedModel("Signal", BridgeField.Bjts, "1e-15 75 1 .025 .025") });
Require(definition.Code == CommandCode.Ok, definition.Message);
Require((await Call(client, "read_named_models")).Models?.Single().Name == "Signal", "Named model was not retained.");
Require((await Call(client, "remove_named_model", new() { ["name"] = "Signal" })).Code == CommandCode.Ok, "Named model removal failed.");
var catalog = await Call(client, "read_catalog");
Require(catalog.Catalog?.Count == 90 &&
    catalog.Catalog.Any(entry => entry.Kind == Kind.CoupledTransmissionLine && entry.Definition.Terminals.Count == 6) &&
    catalog.Catalog.Any(entry => entry.Kind == Kind.PwlFileCurrent && entry.Definition.Prefix == "I"), "Catalog incomplete.");
var initial = (await Call(client, "read_circuit")).Version;
var reset = await Call(client, "reset_lesson", Edit(initial));
Require(reset.Code == CommandCode.Ok, "Reset refused.");
var changed = await Call(client, "set_value", Edit(reset.Version, 3000));
Require(changed.Code == CommandCode.Ok && changed.Version.Revision == reset.Version.Revision + 1, "Edit refused.");
var stale = await Call(client, "set_value", Edit(reset.Version, 9000));
Require(stale.Code == CommandCode.StaleRevision && stale.Version == changed.Version, "Stale edit accepted.");
var invalid = await Call(client, "set_value", Edit(changed.Version, -1));
Require(invalid.Code == CommandCode.InvalidInput && invalid.Version == changed.Version, "Invalid edit accepted.");
var malformed = await client.CallToolAsync("schematic_set_value", Edit(changed.Version), cancellationToken: token);
Require(malformed.IsError == true, "Missing arguments accepted.");
var run = (await Call(client, "run_sweep", Edit(changed.Version))).Run!;
var retained = (await Call(client, "read_run", new() { ["runId"] = run.Id })).Run!;
Require(retained.Version == changed.Version && retained.Samples.Count == 21 &&
    retained.Samples[^1] == new SweepSample(5, 3.75), "Retained result differs.");
await using var other = await Open();
var independent = (await Call(other, "read_circuit")).Circuit!;
Require(independent.Version.DocumentId != initial.DocumentId && independent.Probe == 2.5, "Sessions share state.");
var wrong = await Call(other, "set_value", Edit(changed.Version, 9000));
Require(wrong.Code == CommandCode.WrongDocument, "Another session accepted the edit.");

var sourceWire = new Wire(new("V1", 0), new("R1", 0));
var groundWire = new Wire(new("V1", 1), new("G1", 0));
var bend = new SheetPoint(250, 145);
var route1 = await Change("set_wire_route", changed.Version, ("wire", sourceWire), ("bends", new[] { bend }));
var route2 = await Change("set_wire_route", route1.Version, ("wire", groundWire), ("bends", new[] { bend }));
Require((await Call(client, "read_circuit")).Circuit?.Probe == 3.75, "Crossing joined implicitly.");
var joined = await Change("join_routes", route2.Version, ("first", sourceWire), ("second", groundWire), ("at", bend));
var joinedCircuit = (await Call(client, "read_circuit")).Circuit!;
Require(joinedCircuit is { Probe: null, Junctions.Count: 1 }, "Explicit join did not change connectivity.");
var split = await Change("delete_junction", joined.Version, ("junctionId", joinedCircuit.Junctions![0].Id));
Require((await Call(client, "read_circuit")).Circuit?.Probe == 3.75, "Deleting a junction did not restore connectivity.");

if (nativeEnabled)
{
    var charging = await Call(client, "load_rc", Edit(split.Version));
    var labeled = await Change("set_net_label", charging.Version, ("terminal", new Pin("R1", 1)), ("name", "output"));
    labeled = await Change("set_net_label", labeled.Version, ("terminal", new Pin("P1", 0)), ("name", "OUTPUT"));
    var rewired = await Change("delete_wire", labeled.Version, ("wire", new Wire(new("R1", 1), new("P1", 0))));
    var saved = await Call(client, "save_document");
    Require(saved.Document is not null && saved.Version == rewired.Version, "Save changed the revision.");
    var cleared = await Call(client, "reset_lesson", Edit(rewired.Version));
    var staleLoad = Edit(rewired.Version);
    staleLoad["document"] = saved.Document;
    Require((await Call(client, "load_document", staleLoad)).Code == CommandCode.StaleRevision, "Stale load accepted.");
    charging = await Change("load_document", cleared.Version, ("document", saved.Document));
    Require((await Call(client, "save_document")).Document == saved.Document, "MCP sheet round trip differed.");
    var rcArgs = Edit(charging.Version);
    rcArgs["probeId"] = "P1";
    var solved = await Call(client, "run_transient", rcArgs);
    var waveform = solved.Transient ?? throw new InvalidOperationException(solved.Message);
    var fetched = (await Call(client, "read_run", new() { ["runId"] = waveform.Id })).Transient!;
    Require(waveform.ProbeId == "P1" && fetched.Version == charging.Version &&
        fetched.Samples.SequenceEqual(waveform.Samples), "The retained waveform differs from the probe run.");
    var tau = waveform.Input.TimeConstant;
    var rise = waveform.Input.RiseSeconds;
    double Expected(double t) => t <= rise
        ? waveform.Input.Volts / rise * (t - tau * (1 - Math.Exp(-t / tau)))
        : waveform.Input.Volts * (1 - (tau / rise) * (1 - Math.Exp(-rise / tau)) * Math.Exp(-(t - rise) / tau));
    var maxError = waveform.Samples.Max(s => Math.Abs(s.Volts - Expected(s.Seconds)));
    Require(waveform.Samples.Count > 400 && maxError < 0.01, "The RC waveform differs from the analytical response.");
    var highlight = await Call(client, "highlight_net", new() { ["terminal"] = new Pin("P1", 0) });
    Require(highlight.Net?.Terminals.Count == 3 && highlight.Net.Wires.Count == 1,
        "The labeled probe did not highlight the full output net.");
    var sessionJson = (await Call(client, "save_document")).Document;
    var import = Edit(independent.Version);
    import["document"] = sessionJson;
    Require((await Call(other, "load_document", import)).Code == CommandCode.Ok, "Session import failed.");
    var restoredView = await Call(other, "read_view");
    Require(restoredView.View?.Highlight == new Pin("P1", 0) &&
        restoredView.Net!.Terminals.OrderBy(pin => pin.Part).SequenceEqual(new[]
            { new Pin("C1", 0), new Pin("P1", 0), new Pin("R1", 1) }), "Reloaded net differed.");
    var history = (await Call(other, "read_analysis_history")).History!;
    var replay = (await Call(other, "read_run", new() { ["runId"] = waveform.Id })).Transient!;
    Require(history.Current.Contains(waveform.Id) && replay.Version == waveform.Version &&
        replay.Samples.SequenceEqual(waveform.Samples), "Reload changed RC history or provenance.");
    Console.WriteLine("MCP restored the whole net and exact RC history into a second session.");
    var removedCap = await Change("delete_part", charging.Version, ("partId", "C1"));
    var polar = await Change("place_part", removedCap.Version, ("kind", "PolarizedCapacitor"), ("x", 350), ("y", 215));
    var polarId = polar.Part!.Id;
    var polarIn = await Change("connect_pins", polar.Version, ("firstPart", polarId), ("firstPin", 0),
        ("secondPart", "R1"), ("secondPin", 1));
    var polarOut = await Change("connect_pins", polarIn.Version, ("firstPart", polarId), ("firstPin", 1),
        ("secondPart", "G1"), ("secondPin", 0));
    rcArgs = Edit(polarOut.Version);
    rcArgs["probeId"] = "P1";
    var polarRun = (await Call(client, "run_transient", rcArgs)).Transient;
    Require(polarRun is not null && polarRun.Samples.SequenceEqual(waveform.Samples),
        "Forward polarized capacitor changed the ideal RC waveform.");
    Console.WriteLine("MCP verified the forward polarized capacitor with the native solver.");
    rcArgs["probeId"] = "missing";
    Require((await Call(client, "run_transient", rcArgs)).Code == CommandCode.UnsupportedCircuit,
        "A missing probe was accepted.");
    Console.WriteLine($"Native RC probe returned {waveform.Samples.Count} samples with maximum error {maxError:G6} V.");
}
if (nativeEnabled)
{
    var separate = (await Call(other, "read_circuit")).Version;
    var demo = await Call(other, "load_bridge_demo", Edit(separate));
    foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
    {
        var arguments = Edit(demo.Version);
        arguments["options"] = BridgeDemo.Options(analysis);
        var solved = await Call(other, "run_circuit", arguments);
        var bridge = solved.Bridge ?? throw new InvalidOperationException($"{analysis}: {solved.Message}");
        Require(bridge.Version == demo.Version && bridge.Analysis == analysis &&
            (analysis != BridgeAnalysis.Ac || bridge.Samples.Any(point => point.Imaginary != 0)), "Wrong circuit run.");
        Console.WriteLine($"MCP mixed {analysis}: {bridge.Samples.Count} samples, final {bridge.Samples[^1]}.");
    }
    var sourceDemo = await Call(other, "reset_lesson", Edit(demo.Version));
    foreach (var profile in new[] { "waveforms", "exponential", "sine" })
    {
        var arguments = Edit(sourceDemo.Version);
        arguments["options"] = BridgeDemo.WaveformOptions(profile);
        var result = await Call(other, "run_circuit", arguments);
        Require(result.Bridge is { Samples.Count: > 1000 }, $"Waveforms refused: {result.Message}");
        Console.WriteLine($"MCP transient {profile} completed.");
    }
    var models = await Call(other, "load_model_demo", Edit(sourceDemo.Version));
    foreach (var probe in new[] { "P1", "P2" })
    foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
    {
        var arguments = Edit(models.Version);
        arguments["options"] = BridgeDemo.ModelOptions(analysis, probe);
        var solved = await Call(other, "run_circuit", arguments);
        var bridge = solved.Bridge ?? throw new InvalidOperationException($"{probe} {analysis}: {solved.Message}");
        Require(bridge.ProbeId == probe && bridge.Analysis == analysis, "Wrong semiconductor run.");
        Console.WriteLine($"MCP semiconductor {probe} {analysis}: {bridge.Samples.Count} samples.");
    }
    if (Environment.GetEnvironmentVariable("SCHEMATIC_MEYER_ORACLE") is { } oracle)
    {
        double[] Reference(string name, bool last = false)
        {
            var lines = File.ReadLines(Path.Combine(oracle, name));
            return (last ? lines.Last() : lines.First()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }

        foreach (var analysis in new[] { BridgeAnalysis.Ac, BridgeAnalysis.Transient })
        {
            var meyerRequest = Edit(models.Version);
            meyerRequest["options"] = BridgeDemo.MeyerOptions(analysis);
            var response = await Call(other, "run_circuit", meyerRequest);
            var meyerRun = response.Bridge ?? throw new InvalidOperationException(response.Message);
            if (analysis == BridgeAnalysis.Ac)
            {
                foreach (var (sample, file) in meyerRun.Samples.Zip(new[] { "ac-low.dat", "ac-high.dat" }))
                {
                    var expected = Reference(file);
                    Require(sample.Axis == expected[0] && Math.Abs(sample.Real - expected[1]) < 1e-5 &&
                        Math.Abs(sample.Imaginary - expected[2]) < 1e-5, "Meyer complex AC disagreement.");
                }
            }
            else
            {
                var sample = meyerRun.Samples[^1];
                var expected = Reference("transient.dat", last: true);
                Require(Math.Abs(sample.Axis - expected[0]) < 1e-11 && Math.Abs(sample.Real - expected[1]) < 5e-4,
                    "Meyer transient endpoint disagreement.");
            }
        }
        Console.WriteLine("MCP matched ngspice AC and transient samples with intrinsic MOS1 charge.");
    }
    var high1 = await Call(other, "set_value", ValueEdit(models.Version, "V1", 3));
    var high2 = await Call(other, "set_value", ValueEdit(high1.Version, "V3", 1));
    var highOptions = BridgeDemo.ModelOptions(BridgeAnalysis.Dc, "P2");
    var highArgs = Edit(high2.Version);
    highArgs["options"] = highOptions with
    {
        UseSourceContinuation = true,
        Diodes = [highOptions.Diodes![0] with { JunctionFarads = 0, TransitSeconds = 0 }]
    };
    var highRun = await Call(other, "run_circuit", highArgs);
    Require(highRun.Bridge is { Samples.Count: 1 }, $"Source stepping refused: {highRun.Message}");
    Console.WriteLine("MCP recovered the higher-bias MOS1 operating point.");
    var hierarchy = await Call(other, "load_hierarchy_demo", Edit(high2.Version));
    var child = (await Call(other, "read_child_sheet", new() { ["cellId"] = "X1" })).Child;
    Require(child is not null, "Nested child was missing.");
    var attach = Edit(hierarchy.Version);
    attach["cellId"] = "X1";
    attach["child"] = child;
    var attached = await Call(other, "attach_child_sheet", attach);
    Require(attached.Code == CommandCode.Ok, attached.Message);
    Require((await Call(other, "attach_child_sheet", attach)).Code == CommandCode.StaleRevision,
        "Stale child replacement was accepted.");
    var hierarchyRun = Edit(attached.Version);
    hierarchyRun["options"] = new BridgeOptions(BridgeAnalysis.Dc, "P1");
    var hierarchyResult = await Call(other, "run_circuit", hierarchyRun);
    Require(Math.Abs((hierarchyResult.Bridge?.Samples.Single().Real ?? 0) - 3.75) < 1e-9, hierarchyResult.Message);
    Console.WriteLine("MCP attached and solved the nested divider at 3.75 V.");
    var bjt = await Call(other, "load_bjt_demo", Edit(attached.Version));
    foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
    {
        var arguments = Edit(bjt.Version);
        arguments["options"] = BridgeDemo.BjtOptions(analysis);
        var solved = await Call(other, "run_circuit", arguments);
        var runBjt = solved.Bridge ?? throw new InvalidOperationException($"BJT {analysis}: {solved.Message}");
        Console.WriteLine($"MCP BJT {analysis}: {runBjt.Samples.Count} samples.");
    }
    var appliedFields = await Call(other, "apply_analysis_fields", new() { ["fields"] = new Dictionary<string, string> { ["Pwl"] = "V2 0 .55 1e-5 .68" } });
    Require(appliedFields.Code == CommandCode.Ok, "Recipe fields refused.");
    var recipe = (await Call(other, "save_document")).Document!;
    await using (var imported = await Open())
    {
        var target = Edit((await Call(imported, "read_circuit")).Version);
        target["document"] = recipe;
        Require((await Call(imported, "load_document", target)).Code == CommandCode.Ok, "Recipe reload failed.");
        var fields = (await Call(imported, "read_analysis_fields")).AnalysisFields!;
        Require(fields[BridgeField.Pwl] == "V2 0 .55 1e-5 .68" && fields[BridgeField.Bjts].Contains("3e-10"),
            "Reload did not restore the source and model fields.");
    }
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPICE_BSIM330_HOST")))
    {
        var bulk = await Call(other, "load_bsim3_demo", Edit(bjt.Version));
        foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
        {
            var arguments = Edit(bulk.Version);
            arguments["options"] = BridgeDemo.Bsim3Options(analysis);
            var solved = await Call(other, "run_circuit", arguments);
            var runBulk = solved.Bridge ?? throw new InvalidOperationException($"BSIM3 {analysis}: {solved.Message}");
            Console.WriteLine($"MCP BSIM3 {analysis}: {runBulk.Samples.Count} samples.");
        }
    }
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPICE_BSIM_SOI_FD_HOST")))
    {
        var currentSoiVersion = (await Call(other, "read_circuit")).Version;
        var soi = await Call(other, "load_soi_demo", Edit(currentSoiVersion));
        var arguments = Edit(soi.Version);
        arguments["options"] = BridgeDemo.SoiOptions();
        var result = await Call(other, "run_circuit", arguments);
        Require(result.Bridge is { Samples.Count: 1 }, $"FD-SOI refused: {result.Message}");
        arguments["options"] = BridgeDemo.SoiOptions() with { Analysis = BridgeAnalysis.Ac, FrequenciesHz = [1000] };
        Require((await Call(other, "run_circuit", arguments)).Code == CommandCode.UnsupportedCircuit, "FD-SOI accepted AC.");
        Console.WriteLine("MCP FD-SOI DC passed and unsupported AC was refused.");
    }
}
var current = (await Call(client, "read_circuit")).Version;
var geometry = Edit(current);
geometry["partId"] = "R1";
geometry["x"] = 200;
geometry["y"] = 75;
var moved = await Call(client, "move_part", geometry);
Require(moved.Code == CommandCode.Ok, "Move refused.");
var undone = await Call(client, "undo_edit", Edit(moved.Version));
Require(undone.Code == CommandCode.Ok && undone.Version.Revision == moved.Version.Revision + 1, "Undo failed.");
Require((await Call(client, "redo_edit", Edit(moved.Version))).Code == CommandCode.StaleRevision, "Stale redo accepted.");
var redone = await Call(client, "redo_edit", Edit(undone.Version));
Require(redone.Code == CommandCode.Ok, "Redo refused.");
var removeGround = Edit(redone.Version);
removeGround["partId"] = "G1";
var deleted = await Call(client, "delete_part", removeGround);
Require(deleted.Code == CommandCode.Ok, "Ground deletion refused.");
var findings = (await Call(client, "check_circuit")).Findings!;
Require(findings.Any(f => f.Code == FindingCode.MissingGround && f.Blocking) &&
    findings.Any(f => f.Terminal == new Pin("V1", 1)), "Preflight findings missing.");
Console.WriteLine("MCP geometry, history, stale redo and preflight passed.");
Console.WriteLine("MCP tools discovered. Divider returned 3.75 V. Stale, invalid and cross-session edits refused.");

async Task<McpClient> Open() => await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Name = "SchematicLab",
    Command = "dotnet",
    Arguments = [Path.GetFullPath(args[0])]
}), cancellationToken: token);

async Task<CommandResult> Call(McpClient connection, string name, Dictionary<string, object?>? arguments = null)
{
    var result = await connection.CallToolAsync("schematic_" + name, arguments, cancellationToken: token);
    if (result.IsError == true || result.StructuredContent is not { } body)
        throw new InvalidOperationException($"Tool {name} failed: {JsonSerializer.Serialize(result)}");
    return body.Deserialize<CommandResult>(options) ?? throw new InvalidOperationException("Missing command result.");
}

async Task<CommandResult> Change(string name, DocumentVersion version, params (string Key, object? Value)[] values)
{
    var arguments = Edit(version);
    foreach (var (key, value) in values) arguments[key] = value;
    var result = await Call(client, name, arguments);
    Require(result.Code == CommandCode.Ok, $"{name} refused: {result.Message}");
    return result;
}

static Dictionary<string, object?> Edit(DocumentVersion version, double? value = null)
{
    var arguments = new Dictionary<string, object?>
    {
        ["documentId"] = version.DocumentId, ["revision"] = version.Revision
    };
    if (value is double voltageOrResistance)
    {
        arguments["partId"] = "R2";
        arguments["value"] = voltageOrResistance;
    }
    return arguments;
}

static Dictionary<string, object?> ValueEdit(DocumentVersion version, string id, double value)
{
    var fields = Edit(version);
    fields["partId"] = id;
    fields["value"] = value;
    return fields;
}

static void Require(bool valid, string reason)
{
    if (!valid) throw new InvalidOperationException(reason);
}
