using System.Text;

namespace Einsatzcockpit.PrintAgent.Core.Printing;

public static class Pjl
{
    public const string Uel = "\u001b%-12345X";

    public static byte[] Build(string documentName, int copies, string? duplex, string? media, bool color)
    {
        var binding = PrintValueMappings.Duplex(duplex);
        var lines = new StringBuilder(Uel)
            .Append("@PJL JOB NAME=\"").Append(Escape(documentName)).AppendLine("\"")
            .Append("@PJL SET COPIES=").Append(Math.Max(1, copies)).AppendLine()
            .Append("@PJL SET DUPLEX=").Append(binding == "off" ? "OFF" : "ON").AppendLine();
        if (binding != "off")
            lines.Append("@PJL SET BINDING=").Append(binding == "long-edge" ? "LONGEDGE" : "SHORTEDGE").AppendLine();
        lines.Append("@PJL SET PAPER=").Append(PrintValueMappings.Media(media)).AppendLine()
            .Append("@PJL SET RENDERMODE=").Append(color ? "COLOR" : "GRAYSCALE").AppendLine()
            .AppendLine("@PJL ENTER LANGUAGE=PDF");
        return Encoding.ASCII.GetBytes(lines.ToString());
    }

    public static byte[] End() => Encoding.ASCII.GetBytes(Uel + "@PJL EOJ\r\n" + Uel);
    private static string Escape(string value) => value.Replace("\"", "'");
}
