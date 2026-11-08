#:property PublishAot=false
#:package ModelContextProtocol@2.4.0
#:project ../Core/SchematicLab.Core.csproj
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using SchematicLab.Core;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the absolute path to the built MCP host DLL.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = deadline.Token;
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
options.Converters.Add(new JsonStringEnumConverter());
await using var client = await Open();
var tools = await client.ListToolsAsync(cancellationToken: token);
var expectedTools = new[] { "read_circuit", "read_catalog", "set_value", "place_part", "connect_pins",
    "delete_part", "reset_lesson", "run_sweep", "read_run", "load_rc", "run_transient", "move_part", "rotate_part", "mirror_part", "duplicate_part", "undo_edit", "redo_edit", "check_circuit", "set_net_label", "set_wire_route", "delete_wire", "join_routes", "delete_junction", "save_document", "load_document", "highlight_net", "read_view", "read_analysis_history", "load_bridge_demo", "load_model_demo", "load_bjt_demo", "run_circuit" };
Require(tools.Select(tool => tool.Name).Order().SequenceEqual(
    expectedTools.Select(name => "schematic_" + name).Order()), "Tool allowlist mismatch.");

var catalog = await Call(client, "read_catalog");
Require(catalog.Catalog?.Count == 22, "Catalog incomplete.");
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

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH")))
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
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH")))
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
    var models = await Call(other, "load_model_demo", Edit(demo.Version));
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
    var bjt = await Call(other, "load_bjt_demo", Edit(models.Version));
    foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
    {
        var arguments = Edit(bjt.Version);
        arguments["options"] = BridgeDemo.BjtOptions(analysis);
        var solved = await Call(other, "run_circuit", arguments);
        var runBjt = solved.Bridge ?? throw new InvalidOperationException($"BJT {analysis}: {solved.Message}");
        Console.WriteLine($"MCP BJT {analysis}: {runBjt.Samples.Count} samples.");
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

static void Require(bool valid, string reason)
{
    if (!valid) throw new InvalidOperationException(reason);
}
