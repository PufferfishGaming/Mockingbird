using TriAsr.Domain;

namespace TriAsr.Application;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout, Action<string>? ErrorLine = null);
/// <summary><see cref="CpuSeconds"/> is the processor time the process itself used, which is not the same as the wall-clock <see cref="Seconds"/>.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, double Seconds, long? PeakRamBytes = null, double? CpuSeconds = null);
public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default); }
public sealed record AudioInfo(int SampleRate, int Channels, int BitsPerSample, long SampleCount)
{
    public double DurationSeconds => (double)SampleCount / SampleRate;
}
public interface IAudioNormalizer { Task<AudioInfo> NormalizeAsync(string source, string destination, CancellationToken cancellationToken); }
public interface IJobRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TranscriptionJob job, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptionJob>> ListAsync(CancellationToken cancellationToken = default);
}
