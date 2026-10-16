using System.Buffers.Binary;
using System.Text;
using Einsatzcockpit.PrintAgent.Core.Printing;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class PrintingHelpersTests
{
    [Fact]
    public void Pjl_enthaelt_alle_Druckoptionen_und_uel() {
        var text = Encoding.ASCII.GetString(Pjl.Build("Lage \"1\"", 2, "long-edge", "A3", false));
        Assert.StartsWith(Pjl.Uel, text); Assert.Contains("COPIES=2", text); Assert.Contains("DUPLEX=ON", text); Assert.Contains("BINDING=LONGEDGE", text); Assert.Contains("PAPER=A3", text); Assert.Contains("RENDERMODE=GRAYSCALE", text); Assert.EndsWith(Pjl.Uel, Encoding.ASCII.GetString(Pjl.End()));
    }
    [Fact]
    public void Ipp_PrintJob_enthält_Operation_und_Attribute() {
        var bytes = IppMessage.Encode(IppMessage.PrintJob, 7, [new(0x47, "attributes-charset", "utf-8"), new(0x45, "printer-uri", "ipp://drucker/ipp")]);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2))); Assert.Contains(Encoding.UTF8.GetBytes("printer-uri"), bytes);
    }
    [Fact]
    public void Ipp_Response_liefert_Status_und_JobId() {
        var response = IppMessage.Decode([1,1,0,0,0,0,0,3,1,0x21,0,6,(byte)'j',(byte)'o',(byte)'b',(byte)'-',(byte)'i',(byte)'d',0,4,0,0,0,42,3]);
        Assert.Equal((ushort)0, response.StatusCode); Assert.Equal(42, response.Attributes["job-id"]);
    }
    [Fact]
    public void Argumente_und_Dateiname_bleiben_sicher() {
        var arguments = PrintPathHelpers.ExpandCommandArguments(["--file", "{pdf}"], new Dictionary<string,string> { ["pdf"] = "C:\\mit leerzeichen\\a.pdf" });
        Assert.Equal("C:\\mit leerzeichen\\a.pdf", arguments[1]); Assert.DoesNotContain(':', PrintPathHelpers.SafeFileName("a:b?.pdf")); Assert.Equal("short-edge", PrintValueMappings.Duplex("short")); Assert.Equal("A4", PrintValueMappings.Media("foo"));
    }
}
