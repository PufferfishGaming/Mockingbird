using System.Text.Json;
using System.Security.Cryptography;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Infrastructure;

public sealed class JobWorkspace(IStoragePaths paths) : IJobWorkspace
{
    public string DirectoryFor(Guid id) => Path.Combine(paths.Root, "Jobs", id.ToString("N"));
    public async Task CreateAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(job.Id);
        Directory.CreateDirectory(directory);
        await using var source = File.OpenRead(job.SourcePath);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
        await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), JsonSerializer.Serialize(new
        {
            job.Id, job.SourcePath, job.Language, job.CreatedUtc, Length = new FileInfo(job.SourcePath).Length,
            LastWriteUtc = File.GetLastWriteTimeUtc(job.SourcePath), Sha256 = digest
        }), cancellationToken);
    }
}
