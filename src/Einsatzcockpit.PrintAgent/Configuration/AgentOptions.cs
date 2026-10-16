namespace Einsatzcockpit.PrintAgent.Configuration;

/// <summary>Kompatibler Namensraum für die lokale Agent-Konfiguration.</summary>
public static class AgentOptions
{
    public static Core.Configuration.AgentOptions Load(AgentPaths paths) =>
        Core.Configuration.AgentConfiguration.Load(paths.ConfigurationPath);

    public static void Save(AgentPaths paths, Core.Configuration.AgentOptions options) =>
        Core.Configuration.AgentConfiguration.Save(paths.ConfigurationPath, options);
}
