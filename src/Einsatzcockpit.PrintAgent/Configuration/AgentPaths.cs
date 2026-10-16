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
    }

    public string DataDirectory { get; }
    public string SpoolDirectory { get; }
    public string LogDirectory { get; }
    public string ConfigurationPath { get; }
    public string TokenPath { get; }
    public string DatabasePath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SpoolDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
