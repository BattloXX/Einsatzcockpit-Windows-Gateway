using Microsoft.Data.Sqlite;

namespace Einsatzcockpit.PrintAgent.Tests;

internal static class TestFiles
{
    /// <summary>Löscht eine Test-SQLite-Datei samt WAL/SHM. Unter Windows hält der
    /// Verbindungspool die Datei sonst offen.</summary>
    public static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // Temp-Datei; ein Rest stört keinen Test.
            }
        }
    }
}
