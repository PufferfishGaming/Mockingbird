using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriAsr.Application;
using TriAsr.Persistence;

namespace TriAsr.Ui.SmokeTests;

public sealed class HostBootstrapTests
{
    [Fact]
    public async Task CompositionStartsOpensSqliteAndFlushesStructuredLocalLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var host = App.App.CreateHost(root))
            {
                await host.StartAsync();
                Assert.Equal(root, host.Services.GetRequiredService<IStoragePaths>().Root);
                Assert.NotNull(host.Services.GetRequiredService<App.BootstrapViewModel>());
                host.Services.GetRequiredService<ILogger<HostBootstrapTests>>()
                    .LogInformation("Bootstrap test event {Stage}", "composition");
                await using var connection = await host.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
                await host.StopAsync();
            }
            var log = Assert.Single(Directory.GetFiles(Path.Combine(root, "Logs"), "*.clef"));
            var content = await File.ReadAllTextAsync(log);
            Assert.Contains("\"Stage\":\"composition\"", content);
            foreach (var line in File.ReadLines(log))
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                Assert.True(document.RootElement.TryGetProperty("@t", out _));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}