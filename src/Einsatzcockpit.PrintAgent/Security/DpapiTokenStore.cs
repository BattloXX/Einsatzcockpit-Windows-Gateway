using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Security;

public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Einsatzcockpit.PrintAgent.Token.v1");
    private readonly string _path;
    private readonly ILogger<DpapiTokenStore> _logger;

    public DpapiTokenStore(string path, ILogger<DpapiTokenStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public string? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(_path);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Save(string token)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(_path, bytes);
        RestrictAccess();
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private void RestrictAccess()
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(_path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ACL für die Token-Datei konnte nicht gesetzt werden.");
        }
    }
}
