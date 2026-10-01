using Microsoft.Extensions.DependencyInjection;
using TriAsr.Infrastructure;
using TriAsr.Persistence;

namespace TriAsr.Persistence.Tests;

public sealed class SqliteBootstrapTests
{
    [Fact]
    public async Task DataSurvivesConnectionReopenAndForeignKeysAreEnforced()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(root);
            paths.EnsureDirectories();
            var services = new ServiceCollection().AddSingleton<TriAsr.Application.IStoragePaths>(paths).AddPersistence();
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<SqliteConnectionFactory>();
            await using (var connection = await factory.OpenAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE bootstrap(value TEXT NOT NULL); INSERT INTO bootstrap VALUES ('persisted');";
                await command.ExecuteNonQueryAsync();
            }
            await using (var reopened = await factory.OpenAsync())
            {
                await using var command = reopened.CreateCommand();
                command.CommandText = "SELECT value FROM bootstrap;";
                Assert.Equal("persisted", await command.ExecuteScalarAsync());
                command.CommandText = "PRAGMA foreign_keys;";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelledOpenDoesNotReturnAnOpenConnection()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(root);
            paths.EnsureDirectories();
            var factory = new SqliteConnectionFactory(paths);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenAsync(cancellation.Token));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}