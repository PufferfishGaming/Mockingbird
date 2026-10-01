using System.IO;

namespace TriAsr.Ui.SmokeTests;

internal static class TestCleanup
{
    /// <summary>
    /// Deletes a temporary data folder. Preference saves run in the background and may still be finishing a write to the
    /// SQLite file when a test ends, so the delete is retried briefly instead of failing a test that already passed.
    /// </summary>
    public static void Delete(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
        }
    }
}
