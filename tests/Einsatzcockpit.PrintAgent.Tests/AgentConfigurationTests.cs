using Einsatzcockpit.PrintAgent.Core.Configuration;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class AgentConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ecpg-config-" + Guid.NewGuid());

    [Theory]
    [InlineData("https://cloud.example", "wss://cloud.example/ws/print-agent")]
    [InlineData("http://cloud.example:8080/base", "ws://cloud.example:8080/ws/print-agent")]
    public void WebSocketUrl_wird_aus_CloudUrl_abgeleitet(string cloudUrl, string expected)
    {
        Assert.Equal(expected, AgentConfiguration.WebSocketUri(cloudUrl).ToString().TrimEnd('/'));
    }

    [Fact]
    public void Fehlende_Konfiguration_liefert_Defaults()
    {
        var options = AgentConfiguration.Load(Path.Combine(_directory, "agent.json"));

        Assert.Equal("Information", options.LogLevel);
        Assert.Empty(options.EffectiveAllowedCommands);
        Assert.Equal(9, options.HtmlReadyTimeoutS);
    }

    [Fact]
    public void Konfiguration_wird_mit_SnakeCase_geparst()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "agent.json");
        File.WriteAllText(path, "{\"cloud_url\":\"https://cloud.example\",\"html_ready_timeout_s\":12}");

        var options = AgentConfiguration.Load(path);

        Assert.Equal("https://cloud.example", options.CloudUrl);
        Assert.Equal(12, options.HtmlReadyTimeoutS);
        Assert.Empty(options.EffectiveAllowedCommands);
    }

    [Fact]
    public void Spool_Dateiname_ist_deterministisch()
    {
        Assert.Equal("auftrag-7-g3.pdf", AgentConfiguration.SpoolFileName("auftrag-7", 3));
    }

    [Theory]
    [InlineData("%PDF-1.7", true)]
    [InlineData("<html>", false)]
    [InlineData("%PD", false)]
    public void Pdf_Signatur_wird_geprueft(string content, bool expected)
    {
        Assert.Equal(expected, AgentConfiguration.HasPdfSignature(System.Text.Encoding.ASCII.GetBytes(content)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
