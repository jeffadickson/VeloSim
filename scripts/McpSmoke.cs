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
    "delete_part", "reset_lesson", "run_sweep", "read_run", "load_rc", "run_transient", "move_part", "rotate_part", "mirror_part", "duplicate_part", "undo_edit", "redo_edit", "check_circuit" };
Require(tools.Select(tool => tool.Name).Order().SequenceEqual(
    expectedTools.Select(name => "schematic_" + name).Order()), "Tool allowlist mismatch.");

var catalog = await Call(client, "read_catalog");
Require(catalog.Catalog?.Count == 19, "Catalog incomplete.");
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
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH")))
{
    var charging = await Call(client, "load_rc", Edit(changed.Version));
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
    rcArgs["probeId"] = "missing";
    Require((await Call(client, "run_transient", rcArgs)).Code == CommandCode.UnsupportedCircuit,
        "A missing probe was accepted.");
    Console.WriteLine($"Native RC probe returned {waveform.Samples.Count} samples with maximum error {maxError:G6} V.");
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
