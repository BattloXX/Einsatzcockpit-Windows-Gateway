using System.Diagnostics;
using Einsatzcockpit.PrintAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Security;

public static class AgentAcl
{
    public static void Apply(AgentPaths paths, ILogger logger)
    {
        TryGrant(paths.StatusPath, "(R)", logger);
        TryGrant(paths.LogDirectory, "(OI)(CI)RX", logger);
        TryGrant(paths.RequestsDirectory, "(OI)(CI)M", logger);
    }

    private static void TryGrant(string path, string rights, ILogger logger)
    {
        try
        {
            var start = new ProcessStartInfo("icacls.exe", $"\"{path}\" /grant *S-1-5-32-545:{rights}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            process?.WaitForExit();
            if (process is null || process.ExitCode != 0)
            {
                logger.LogWarning("ACL für {Pfad} konnte nicht gesetzt werden.", path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ACL für {Pfad} konnte nicht gesetzt werden.", path);
        }
    }
}
