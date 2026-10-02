using System.Globalization;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Persistence;

public sealed class JobRepository(SqliteConnectionFactory connections) : IJobRepository
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_versions(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
            INSERT OR IGNORE INTO schema_versions VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, source_path TEXT NOT NULL, state INTEGER NOT NULL, created_utc TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS job_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL REFERENCES jobs(id), state INTEGER NOT NULL, at_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workspace_records(kind TEXT NOT NULL,record_key TEXT NOT NULL,job_id TEXT REFERENCES jobs(id),payload TEXT NOT NULL CHECK(json_valid(payload)),sha256 TEXT NOT NULL,updated_utc TEXT NOT NULL,PRIMARY KEY(kind,record_key));
            CREATE INDEX IF NOT EXISTS records_job_kind ON workspace_records(job_id,kind);
            CREATE VIEW IF NOT EXISTS projects AS SELECT * FROM jobs;
            CREATE VIEW IF NOT EXISTS sources AS SELECT * FROM workspace_records WHERE kind='sources';
            CREATE VIEW IF NOT EXISTS engine_runs AS SELECT * FROM workspace_records WHERE kind='engine_runs';
            CREATE VIEW IF NOT EXISTS alignment AS SELECT * FROM workspace_records WHERE kind='alignment';
            CREATE VIEW IF NOT EXISTS corrections AS SELECT * FROM workspace_records WHERE kind='corrections';
            CREATE VIEW IF NOT EXISTS manual_revisions AS SELECT * FROM workspace_records WHERE kind='manual_revisions';
            CREATE VIEW IF NOT EXISTS benchmarks AS SELECT * FROM workspace_records WHERE kind='benchmarks';
            CREATE VIEW IF NOT EXISTS models AS SELECT * FROM workspace_records WHERE kind='models';
            CREATE VIEW IF NOT EXISTS settings AS SELECT * FROM workspace_records WHERE kind='settings';
            CREATE VIEW IF NOT EXISTS exports AS SELECT * FROM workspace_records WHERE kind='exports';
            CREATE VIEW IF NOT EXISTS segments AS SELECT job_id,json_each.key AS region_index,json_each.value AS payload FROM workspace_records,json_each(workspace_records.payload,'$.Regions') WHERE workspace_records.kind='final';
            INSERT OR IGNORE INTO schema_versions VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task SaveAsync(TranscriptionJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs(id,source_path,state,created_utc,payload) VALUES($id,$source,$state,$created,$payload)
            ON CONFLICT(id) DO UPDATE SET state=excluded.state, payload=excluded.payload;
            INSERT INTO job_events(job_id,state,at_utc) VALUES($id,$state,$now);
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString()); command.Parameters.AddWithValue("$source", job.SourcePath);
        command.Parameters.AddWithValue("$state", (int)job.State); command.Parameters.AddWithValue("$created", job.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job)); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The rows that point at the job go first (foreign keys are on); the settings and models records belong to no job and stay.
        command.CommandText = """
            DELETE FROM job_events WHERE job_id=$id;
            DELETE FROM workspace_records WHERE job_id=$id;
            DELETE FROM jobs WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<TranscriptionJob>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM jobs ORDER BY created_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<TranscriptionJob>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(JsonSerializer.Deserialize<TranscriptionJob>(reader.GetString(0))!);
        return jobs;
    }
}
