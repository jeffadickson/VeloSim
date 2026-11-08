using System.Globalization;

namespace SchematicLab.Core;

public enum BridgeField { Probe, Frequencies, Stop, Step, Phasors, Pulses, Controls, Diodes, Mos1, Bjts, Bsim3 }
public sealed record SetBridgeDraft(BridgeField Field, string Text) : LessonCommand;

/// <summary>Browser entry fields for typed circuit options. Form drafts have no electrical effect.</summary>
public sealed class BridgeDraft
{
    private readonly Dictionary<BridgeField, string> fields = new()
    {
        [BridgeField.Probe] = "P1", [BridgeField.Frequencies] = "100 1000", [BridgeField.Stop] = ".005",
        [BridgeField.Step] = ".00001", [BridgeField.Phasors] = "", [BridgeField.Pulses] = "", [BridgeField.Controls] = "", [BridgeField.Diodes] = "", [BridgeField.Mos1] = "", [BridgeField.Bjts] = "", [BridgeField.Bsim3] = ""
    };

    public string this[BridgeField field] => fields[field];

    internal bool Set(BridgeField field, string? text)
    {
        if (!fields.ContainsKey(field) || text is not { Length: <= 4096 }) return false;
        fields[field] = text;
        return true;
    }

    internal void LoadDemo(bool models)
    {
        fields[BridgeField.Probe] = "P1";
        fields[BridgeField.Frequencies] = "100 1000";
        fields[BridgeField.Stop] = ".003";
        fields[BridgeField.Step] = ".000002";
        fields[BridgeField.Phasors] = "V1 1 30; I1 .0002 90";
        fields[BridgeField.Pulses] = "V1 0 1 0 .00001 .00001 .01 .02; I1 0 .0002 0 .00002 .00001 .01 .02";
        fields[BridgeField.Controls] = "F1 V1; H1 V1";
        fields[BridgeField.Diodes] = "";
        fields[BridgeField.Mos1] = "";
        fields[BridgeField.Bjts] = "";
        fields[BridgeField.Bsim3] = "";
        if (!models) return;
        fields[BridgeField.Stop] = ".0001";
        fields[BridgeField.Step] = ".00000005";
        fields[BridgeField.Phasors] = "V2 1 0; V3 1 0";
        fields[BridgeField.Pulses] = "V2 1.2 1.6 0 .00002 .00002 .005 .01; V3 .2 1 0 .00002 .00002 .005 .01";
        fields[BridgeField.Controls] = "";
        fields[BridgeField.Diodes] = "D1 1e-14 " + (8.617333262145e-5 * 300.15).ToString("R", CultureInfo.InvariantCulture) + " 2e-9 .7 .5 .5 1e-8";
        fields[BridgeField.Mos1] = "M1 .7 .001 0 .6 .02 1e-10 2e-10 5e-11 3e-10 4e-10 .8 .5 .5";
    }

    internal void LoadBjt()
    {
        LoadDemo(false);
        fields[BridgeField.Stop] = ".0001";
        fields[BridgeField.Step] = ".00000005";
        fields[BridgeField.Phasors] = "V2 1 0";
        fields[BridgeField.Pulses] = "V2 .55 .68 0 .00002 .00002 .005 .01";
        fields[BridgeField.Controls] = "";
        var thermal = BridgeDemo.BjtOptions(BridgeAnalysis.Dc).Bjts![0].ForwardThermalVolts.ToString("R", CultureInfo.InvariantCulture);
        fields[BridgeField.Bjts] = $"Q1 1e-15 100 1 {thermal} {thermal}";
    }

    internal void LoadBsim3()
    {
        LoadDemo(false);
        fields[BridgeField.Frequencies] = "1e6 1e9";
        fields[BridgeField.Stop] = "1e-9";
        fields[BridgeField.Step] = "2e-12";
        fields[BridgeField.Phasors] = "V2 1 0";
        fields[BridgeField.Pulses] = "V2 .8 .9 0 1e-9 1e-9 5e-9 1e-8";
        fields[BridgeField.Controls] = "";
        fields[BridgeField.Bsim3] = "M1 3.5e-9 2.4e17 5e16 .15 1e-6 .35e-6 1.5e-6 1.5e-6 1.5e-12 1.5e-12";
    }

    public BridgeOptions? Parse(BridgeAnalysis analysis)
    {
        try
        {
            var controls = Rows(BridgeField.Controls, 2).Select(row => new CurrentControl(row[0], row[1])).ToArray();
            var diodes = Rows(BridgeField.Diodes, 3, 8).Select(row => row.Length == 3
                ? new DiodeBinding(row[0], Number(row[1]), Number(row[2]))
                : new DiodeBinding(row[0], Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]),
                    Number(row[5]), Number(row[6]), Number(row[7]))).ToArray();
            var mos = Rows(BridgeField.Mos1, 6, 14).Select(row => row.Length == 6
                ? new Mos1Binding(row[0], Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]), Number(row[5]))
                : new Mos1Binding(row[0], Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]), Number(row[5]),
                    Number(row[6]), Number(row[7]), Number(row[8]), Number(row[9]), Number(row[10]), Number(row[11]),
                    Number(row[12]), Number(row[13]))).ToArray();
            var bjts = Rows(BridgeField.Bjts, 6).Select(row => new BjtBinding(row[0], Number(row[1]), Number(row[2]),
                Number(row[3]), Number(row[4]), Number(row[5]))).ToArray();
            var bsim3 = Rows(BridgeField.Bsim3, 11).Select(row => new Bsim3Binding(row[0], Number(row[1]), Number(row[2]),
                Number(row[3]), Number(row[4]), Number(row[5]), Number(row[6]), Number(row[7]),
                Number(row[8]), Number(row[9]), Number(row[10]))).ToArray();
            var options = new BridgeOptions(analysis, this[BridgeField.Probe], Controls: controls, Diodes: diodes, Mos1: mos, Bjts: bjts, Bsim3: bsim3);
            if (analysis == BridgeAnalysis.Dc) return options;
            if (analysis == BridgeAnalysis.Ac)
            {
                var phasors = Rows(BridgeField.Phasors, 3).Select(row => new SourcePhasor(row[0], Number(row[1]), Number(row[2]))).ToArray();
                return options with { FrequenciesHz = Tokens(this[BridgeField.Frequencies]).Select(Number).ToArray(),
                    Phasors = phasors };
            }

            var pulses = Rows(BridgeField.Pulses, 8).Select(row => new SourcePulse(row[0], Number(row[1]), Number(row[2]),
                Number(row[3]), Number(row[4]), Number(row[5]), Number(row[6]), Number(row[7]))).ToArray();
            return options with { StopSeconds = Number(this[BridgeField.Stop]),
                StepSeconds = Number(this[BridgeField.Step]), Pulses = pulses };
        }
        catch (FormatException) { return null; }
    }

    private IEnumerable<string[]> Rows(BridgeField field, int count, int alternate = -1)
    {
        foreach (var text in this[field].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var row = Tokens(text);
            if (row.Length != count && row.Length != alternate) throw new FormatException("Wrong source row length.");
            yield return row;
        }
    }

    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
