using System.Security.Cryptography;
using System.Text;
using TriAsr.Application;

namespace TriAsr.Persistence;

public sealed class RecordRepository(SqliteConnectionFactory connections) : IRecordRepository
{
    public async Task SaveAsync(WorkspaceRecord record, CancellationToken token = default)
    {
        await using var connection = await connections.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_records(kind,record_key,job_id,payload,sha256,updated_utc) VALUES($kind,$key,$job,$payload,$hash,$utc)
            ON CONFLICT(kind,record_key) DO UPDATE SET payload=excluded.payload,sha256=excluded.sha256,updated_utc=excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$kind", record.Kind); command.Parameters.AddWithValue("$key", record.Key);
        command.Parameters.AddWithValue("$job", record.JobId?.ToString() ?? (object)DBNull.Value); command.Parameters.AddWithValue("$payload", record.Json);
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.Json))));
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default)
    {
        await using var connection = await connections.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind,record_key,payload,job_id FROM workspace_records WHERE kind=$kind AND ($job IS NULL OR job_id=$job) ORDER BY updated_utc;";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$job", jobId?.ToString() ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token); var records = new List<WorkspaceRecord>();
        while (await reader.ReadAsync(token)) records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3))));
        return records;
    }
}
