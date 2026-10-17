using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Status;
using Einsatzcockpit.PrintAgent.Core.Update;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class UpdateAndStatusTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ecpg-update-" + Guid.NewGuid());

    [Theory]
    [InlineData("2026.10.16")]
    [InlineData("v2026.10.16")]
    [InlineData("2026.10.16.3")]
    [InlineData("2026.10.16.0")]
    public void CalVer_wird_geparst(string value)
    {
        Assert.True(CalVerVersion.TryParse(value, out _));
    }

    [Fact]
    public void Entwicklungsversion_ist_nicht_aktualisierbar()
    {
        var plan = UpdatePlanner.Plan(true, "0.0.0-dev", "2026.10.17", []);
        Assert.False(plan.Install);
    }

    [Fact]
    public void Planer_verschiebt_update_bei_druckuebergabe()
    {
        var job = new StoredJob("j", "t", 1, JobStates.Submitting, "d", "u", "pdf", null, "{}", null,
            null, "name", 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var deferred = UpdatePlanner.Plan(true, "2026.10.16", "2026.10.17", [job]);
        var install = UpdatePlanner.Plan(true, "2026.10.16", "2026.10.17", []);
        Assert.False(deferred.Install);
        Assert.Contains("verschoben", deferred.Message);
        Assert.True(install.Install);
    }

    [Fact]
    public void Planer_ueberspringt_bereits_fehlgeschlagene_version()
    {
        var plan = UpdatePlanner.Plan(true, "2026.10.16", "2026.10.17", [], ["2026.10.17"]);
        Assert.False(plan.Install);
        Assert.Equal("failed", plan.State);
    }

    [Fact]
    public void Wartepolitik_installiert_oder_verschiebt_nach_fuenf_minuten()
    {
        var start = DateTimeOffset.UtcNow;
        Assert.Equal(UpdateWaitDecision.Wait, UpdateWaitPolicy.Decide(true, start, start));
        Assert.Equal(UpdateWaitDecision.Install, UpdateWaitPolicy.Decide(false, start, start));
        Assert.Equal(UpdateWaitDecision.Defer,
            UpdateWaitPolicy.Decide(true, start, start.AddMinutes(5)));
    }

    [Fact]
    public void Release_parser_ignoriert_prerelease_und_fordert_beide_assets()
    {
        using var prerelease = JsonDocument.Parse("""{"tag_name":"v2026.10.17","prerelease":true,"assets":[]}""");
        using var missing = JsonDocument.Parse("""{"tag_name":"v2026.10.17","assets":[]}""");
        Assert.Null(GitHubReleaseClient.ParseRelease(prerelease.RootElement));
        Assert.Null(GitHubReleaseClient.ParseRelease(missing.RootElement));
    }

    [Fact]
    public async Task Statusdatei_ist_lesbar_und_kaputte_datei_liefert_null()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "status.json");
        var file = new StatusFile(path);
        var snapshot = new AgentStatusSnapshot("2026.10.16", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, DateTimeOffset.UtcNow, "https://cloud", "Agent", [], new Dictionary<string, int>(), 0,
            0, null, new UpdateStatus());
        await file.WriteAsync(snapshot);
        Assert.True(file.Read()!.Connected);
        await File.WriteAllTextAsync(path, "{");
        Assert.Null(file.Read());
    }

    [Fact]
    public async Task Pruefsumme_wird_geparst_und_geprueft()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "paket.zip");
        await File.WriteAllTextAsync(path, "inhalt");
        var hash = "494AE86F3433CC1BF42CE17B7BDAD9C7AF03FF2C87CB5A6AC25A53E419ADA74B";
        Assert.True(Sha256Verification.TryGetHash(hash + "  paket.zip", "paket.zip", out var expected));
        Assert.True(await Sha256Verification.VerifyAsync(path, expected, CancellationToken.None));
    }

    [Fact]
    public void Tray_status_bewertet_ampel_tooltip_und_ereignisse()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = Snapshot(true, 0, "2026.10.16");
        var current = Snapshot(false, 1, "2026.10.17");
        var red = TrayStatusEvaluator.Evaluate(current, now, now, previous);
        var green = TrayStatusEvaluator.Evaluate(Snapshot(true, 0, "2026.10.16"), now, now);
        var yellow = TrayStatusEvaluator.Evaluate(Snapshot(true, 1, "2026.10.16"), now, now);
        var stale = TrayStatusEvaluator.Evaluate(Snapshot(true, 0, "2026.10.16"), now.AddMinutes(-1), now);
        Assert.Equal(TrayIndicator.Red, red.Indicator);
        Assert.Equal(TrayIndicator.Green, green.Indicator);
        Assert.Equal(TrayIndicator.Yellow, yellow.Indicator);
        Assert.Equal(TrayIndicator.Red, stale.Indicator);
        Assert.True(red.Tooltip.Length <= 127);
        Assert.Equal(3, red.Events.Count);
    }

    [Fact]
    public void Logfilter_erkennt_level_filtert_und_liefert_ende()
    {
        var lines = new[] { "eins", "[Warning] zwei", "[Error] drei", "[Critical] vier" };
        Assert.Equal(LogLevelKind.Warning, LogFilter.GetLevel(lines[1]));
        Assert.Equal(LogLevelKind.Error, LogFilter.GetLevel(lines[2]));
        Assert.Equal(2, lines.Count(line => LogFilter.Matches(line, LogFilterMode.ErrorsOnly)));
        Assert.Equal(new[] { "[Error] drei", "[Critical] vier" }, LogFilter.Tail(lines, 2));
    }

    private static AgentStatusSnapshot Snapshot(bool connected, int uncertain, string version)
    {
        return new AgentStatusSnapshot(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, connected,
            DateTimeOffset.UtcNow, "https://cloud", "Agent", [new RouteStatus("Ziel", "Drucker", "driver",
                10, true, "ready", null)], new Dictionary<string, int>(), 0, uncertain, null,
            new UpdateStatus("up_to_date", version));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
