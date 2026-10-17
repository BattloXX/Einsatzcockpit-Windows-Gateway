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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
