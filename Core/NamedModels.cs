using System.Globalization;

namespace SchematicLab.Core;

/// <summary>Reusable model parameter row without an instance identifier.</summary>
public sealed record NamedModel(string Name, BridgeField Family, string Parameters);
public sealed record DefineNamedModel(NamedModel Model) : LessonCommand;
public sealed record ApplyNamedModel(string Name, string PartId) : LessonCommand;
public sealed record ReadNamedModels : LessonCommand;
public sealed record RemoveNamedModel(string Name) : LessonCommand;

public sealed partial class LessonSession
{
    private const int ModelLimit = 32;
    private readonly Dictionary<string, NamedModel> namedModels = new(StringComparer.OrdinalIgnoreCase);

    private static bool ValidModel(NamedModel? model)
    {
        if (model is not { Name.Length: > 0 and <= 48, Parameters.Length: > 0 and <= 512 } ||
            !model.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return false;
        var tokens = model.Parameters.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var arity = model.Family switch
        {
            BridgeField.Diodes => tokens.Length is 2 or 7,
            BridgeField.Mos1 => tokens.Length is 5 or 13,
            BridgeField.Bjts => tokens.Length is 5 or 15,
            BridgeField.Bsim3 => tokens.Length == 10,
            _ => false
        };
        return arity && tokens.All(token => double.TryParse(token, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value));
    }

    private CommandResult DefineModel(NamedModel model)
    {
        if (!ValidModel(model)) return Result(CommandCode.InvalidInput, "Enter a valid model name and parameter row.");
        if (namedModels.Count == ModelLimit && !namedModels.ContainsKey(model.Name))
            return Result(CommandCode.LimitReached, "The sheet model library is full.");
        namedModels[model.Name] = model;
        return Result(CommandCode.Ok, $"Stored model {model.Name}.");
    }

    private CommandResult BindModel(string name, string partId)
    {
        if (name is null || !namedModels.TryGetValue(name, out var model))
            return Result(CommandCode.NotFound, "Model not found.");
        var compatible = model.Family switch
        {
            BridgeField.Diodes => FindPart(partId)?.Kind == Kind.Diode,
            BridgeField.Bjts => FindPart(partId)?.Kind is Kind.Npn or Kind.Pnp,
            BridgeField.Mos1 or BridgeField.Bsim3 => FindPart(partId)?.Kind is Kind.Nmos or Kind.Pmos,
            _ => false
        };
        if (!compatible) return Result(CommandCode.InvalidInput, "Choose an instance of the model's device family.");

        var replacement = new Dictionary<BridgeField, string>
        {
            [model.Family] = string.Join("; ", OtherRows(model.Family, partId).Append($"{partId} {model.Parameters}"))
        };
        // Bulk MOS families are mutually exclusive for the same instance.
        if (model.Family is BridgeField.Mos1 or BridgeField.Bsim3)
        {
            var other = model.Family == BridgeField.Mos1 ? BridgeField.Bsim3 : BridgeField.Mos1;
            replacement[other] = string.Join("; ", OtherRows(other, partId));
        }
        return BridgeDraft.Apply(replacement) ? Result(CommandCode.Ok, $"Applied {model.Name} to {partId}.")
            : Result(CommandCode.LimitReached, "The resulting parameter rows exceed the field limit.");
    }

    private IEnumerable<string> OtherRows(BridgeField field, string partId) =>
        BridgeDraft[field].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(row => row.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() != partId);
}
