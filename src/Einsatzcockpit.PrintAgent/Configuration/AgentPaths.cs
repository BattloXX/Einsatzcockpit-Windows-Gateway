namespace Einsatzcockpit.PrintAgent.Configuration;

public sealed class AgentPaths
{
    public AgentPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? Environment.GetEnvironmentVariable("ECPA_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Einsatzcockpit",
                "PrintAgent");
        SpoolDirectory = Path.Combine(DataDirectory, "spool");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        ConfigurationPath = Path.Combine(DataDirectory, "agent.json");
        TokenPath = Path.Combine(DataDirectory, "token.bin");
        DatabasePath = Path.Combine(DataDirectory, "agent.db");
        StatusPath = Path.Combine(DataDirectory, "status.json");
        RequestsDirectory = Path.Combine(DataDirectory, "requests");
        UpdateRequestPath = Path.Combine(RequestsDirectory, "update-request");
        UpdatesDirectory = Path.Combine(DataDirectory, "updates");
        UpdatePendingPath = Path.Combine(DataDirectory, "update-pending");
        UpdateFailedPath = Path.Combine(DataDirectory, "update-failed.json");
    }

    public string DataDirectory { get; }
    public string SpoolDirectory { get; }
    public string LogDirectory { get; }
    public string ConfigurationPath { get; }
    public string TokenPath { get; }
    public string DatabasePath { get; }
    public string StatusPath { get; }
    public string RequestsDirectory { get; }
    public string UpdateRequestPath { get; }
    public string UpdatesDirectory { get; }
    public string UpdatePendingPath { get; }
    public string UpdateFailedPath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SpoolDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RequestsDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
    }
}
