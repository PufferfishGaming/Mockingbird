using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.Application;

namespace TriAsr.Persistence;

/// <summary>Connection infrastructure only. Job schema and migrations begin in milestone 2.</summary>
public sealed class SqliteConnectionFactory(IStoragePaths paths)
{
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 15
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public static class ServiceRegistration
{
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddSingleton<SqliteConnectionFactory>();
        return services;
    }
}