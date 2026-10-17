using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Configuration;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Status;
using Einsatzcockpit.PrintAgent.Core.Update;
using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Update;

public sealed class UpdateService(AgentPaths paths, ILogger<UpdateService> logger, StatusFile statusFile)
{
    public void DeleteStalePendingMarker()
    {
        if (!File.Exists(paths.UpdatePendingPath)
            || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(paths.UpdatePendingPath) < TimeSpan.FromMinutes(15))
        {
            return;
        }

        File.Delete(paths.UpdatePendingPath);
        logger.LogWarning("Verwaister Update-Marker wurde gelöscht.");
    }

    public async Task CheckAsync(Einsatzcockpit.PrintAgent.Core.Configuration.AgentOptions options,
        CancellationToken cancellationToken)
    {
        DeleteStalePendingMarker();
        var now = DateTimeOffset.UtcNow;
        if (File.Exists(paths.UpdatePendingPath))
        {
            statusFile.SetUpdateStatus(new UpdateStatus("installing", LastCheckAt: now,
                Message: "Ein Update wird bereits installiert."));
            return;
        }

        statusFile.SetUpdateStatus(new UpdateStatus("checking", LastCheckAt: now));
        try
        {
            using var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(options.Proxy))
            {
                handler.Proxy = new WebProxy(options.Proxy);
            }

            using var client = new HttpClient(handler);
            var release = await new GitHubReleaseClient(client).GetLatestAsync(options.UpdateRepo, cancellationToken);
            if (release is null)
            {
                statusFile.SetUpdateStatus(new UpdateStatus("up_to_date", LastCheckAt: now,
                    Message: "Kein vollständiges Release gefunden."));
                return;
            }

            statusFile.SetUpdateStatus(new UpdateStatus("available", release.Version, now));
            var current = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
            var plan = UpdatePlanner.Plan(options.AutoUpdate, current, release.Version, ActiveJobs(),
                ReadFailedVersions());
            if (!plan.Install)
            {
                statusFile.SetUpdateStatus(new UpdateStatus(plan.State, release.Version, now, plan.Message));
                return;
            }

            await DownloadAndInstallAsync(client, release, now, cancellationToken);
        }
        catch (Exception ex)
        {
            statusFile.SetUpdateStatus(new UpdateStatus("failed", LastCheckAt: now, Message: ex.Message));
            logger.LogError(ex, "Updateprüfung fehlgeschlagen.");
        }
    }

    private async Task DownloadAndInstallAsync(HttpClient client, ReleaseInfo release, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        statusFile.SetUpdateStatus(new UpdateStatus("downloading", release.Version, now));
        var target = Path.Combine(paths.UpdatesDirectory, release.Version);
        Directory.CreateDirectory(target);
        var zipName = Path.GetFileName(release.ZipUrl.LocalPath);
        var zipPath = Path.Combine(target, zipName);
        var sums = await client.GetStringAsync(release.Sha256SumsUrl, cancellationToken);
        await using (var source = await client.GetStreamAsync(release.ZipUrl, cancellationToken))
        await using (var destination = File.Create(zipPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        if (!Sha256Verification.TryGetHash(sums, zipName, out var expected)
            || !await Sha256Verification.VerifyAsync(zipPath, expected, cancellationToken))
        {
            File.Delete(zipPath);
            throw new InvalidOperationException("SHA256-Prüfung des Updatepakets fehlgeschlagen.");
        }

        File.WriteAllText(Path.Combine(target, "SHA256SUMS.txt"), sums);
        ZipFile.ExtractToDirectory(zipPath, target, true);
        var script = Path.Combine(target, "update.ps1");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException("update.ps1 fehlt im Updatepaket.");
        }

        if (!await WaitForSafeInstallationAsync(release, now, cancellationToken))
        {
            return;
        }

        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -PackageDir \"{target}\" "
            + $"-InstallDir \"{installDirectory}\" -Version \"{release.Version}\"";
        Process.Start(new ProcessStartInfo("powershell.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        logger.LogInformation("Update auf {Version} wird installiert.", release.Version);
    }

    private async Task<bool> WaitForSafeInstallationAsync(ReleaseInfo release, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(paths.UpdatePendingPath, release.Version);
        var pendingSince = DateTimeOffset.UtcNow;
        while (true)
        {
            var decision = UpdateWaitPolicy.Decide(ActiveJobs().Count > 0, pendingSince, DateTimeOffset.UtcNow);
            if (decision == UpdateWaitDecision.Wait)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            if (decision == UpdateWaitDecision.Defer)
            {
                File.Delete(paths.UpdatePendingPath);
                statusFile.SetUpdateStatus(new UpdateStatus("available", release.Version, now,
                    "Update wegen laufender Druckübergabe verschoben."));
                return false;
            }

            var current = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
            var plan = UpdatePlanner.Plan(true, current, release.Version, ActiveJobs(), ReadFailedVersions());
            if (!plan.Install)
            {
                File.Delete(paths.UpdatePendingPath);
                statusFile.SetUpdateStatus(new UpdateStatus(plan.State, release.Version, now, plan.Message));
                return false;
            }

            statusFile.SetUpdateStatus(new UpdateStatus("installing", release.Version, now));
            return true;
        }
    }

    private IReadOnlyCollection<StoredJob> ActiveJobs()
    {
        return new JobStore(paths.DatabasePath).GetByState(JobStates.Granted, JobStates.Fetching,
            JobStates.IntentPending, JobStates.Submitting, JobStates.Spooled);
    }

    private IReadOnlyCollection<string> ReadFailedVersions()
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(paths.UpdateFailedPath)) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
