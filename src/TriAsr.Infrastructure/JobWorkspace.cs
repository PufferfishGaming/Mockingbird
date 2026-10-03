using System.Text.Json;
using System.Security.Cryptography;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Infrastructure;

public sealed class JobWorkspace(IStoragePaths paths) : IJobWorkspace
{
    public string DirectoryFor(Guid id) => Path.Combine(paths.Root, "Jobs", id.ToString("N"));

    /// <summary>The file that keeps what was chosen for a job beyond its language. A job without it was given nothing else.</summary>
    public const string OptionsFile = "options.json";

    public async Task CreateAsync(TranscriptionJob job, JobOptions options, CancellationToken cancellationToken)
    {
        await CreateAsync(job, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(DirectoryFor(job.Id), OptionsFile), JsonSerializer.Serialize(options), cancellationToken);
    }
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
