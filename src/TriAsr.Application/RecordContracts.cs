namespace TriAsr.Application;

public sealed record WorkspaceRecord(string Kind, string Key, string Json, Guid? JobId = null);
public interface IRecordRepository
{
    Task SaveAsync(WorkspaceRecord record, CancellationToken token = default);
    Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default);
}
