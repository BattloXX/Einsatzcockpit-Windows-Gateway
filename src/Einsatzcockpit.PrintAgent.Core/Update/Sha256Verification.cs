using System.Security.Cryptography;

namespace Einsatzcockpit.PrintAgent.Core.Update;

public static class Sha256Verification
{
    public static bool TryGetHash(string sums, string fileName, out string hash)
    {
        hash = string.Empty;
        foreach (var line in sums.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.Ordinal)
                && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                hash = parts[0].ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> VerifyAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(actual), expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
