using System.Text;

namespace VeloSim.Uhdm;

/// <summary>
/// Byte-for-byte port of <c>native/uhdm/json_escape.h</c> (<c>velosim::json_escape</c>).
/// Ensures exact <c>diff -u</c> JSON parity between pure .NET 11 UHDM tools and C++ <c>velosim-uhdm-*</c> binaries.
/// </summary>
internal static class UhdmJson
{
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        AppendEscaped(sb, text);
        return sb.ToString();
    }

    public static void AppendEscaped(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u00");
                        sb.Append("0123456789abcdef"[(ch >> 4) & 0xF]);
                        sb.Append("0123456789abcdef"[ch & 0xF]);
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
