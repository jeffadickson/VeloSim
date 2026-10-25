using System.Text.Json;
namespace VeloSim;
public sealed partial class RtlDesign
{
    // A call argument/local assignment is a self-determined sizing boundary.
    // Signedness belongs to the destination declaration after conversion.
    private int ValidateConversion(JsonElement e,int depth)
    {
        JsonShape.Fields(e,["kind","value","width","signed","two_state"]);
        ValidateExpression(e.GetProperty("value"),depth+1);
        int width=e.GetProperty("width").GetInt32();_=e.GetProperty("signed").GetBoolean();_=e.GetProperty("two_state").GetBoolean();
        if(width is <1 or >128)throw new IrValidationException("Call conversion width must be 1..128.");return width;
    }
    private string Conversion(JsonElement e)
    {
        int width=e.GetProperty("width").GetInt32();string value=$"resize({Expression(e.GetProperty("value"),width)},{width})";
        return e.GetProperty("two_state").GetBoolean()?$"two_state({value})":value;
    }
}
