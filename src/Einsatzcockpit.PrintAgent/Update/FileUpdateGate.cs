using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Update;

namespace Einsatzcockpit.PrintAgent.Update;

public sealed class FileUpdateGate(AgentPaths paths) : IUpdateGate
{
    public bool IsUpdatePending
    {
        get
        {
            if (!File.Exists(paths.UpdatePendingPath))
            {
                return false;
            }

            return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(paths.UpdatePendingPath)
                < TimeSpan.FromMinutes(15);
        }
    }
}
