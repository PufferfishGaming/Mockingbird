namespace TriAsr.Hardware;

/// <summary>A hardware probe that threw. Distinct from absent hardware, which is a plain empty result.</summary>
public sealed record ProbeError(string Probe, string ExceptionType, string Message);

/// <summary>
/// Runs one hardware probe at a time. A probe that throws yields its default value AND an entry in
/// <see cref="Errors"/>, so "the probe failed" can be told apart from "the hardware is not there".
/// </summary>
public sealed class ProbeRunner
{
    private const int MaxMessageLength = 300;
    private readonly List<ProbeError> _errors = [];

    public IReadOnlyList<ProbeError> Errors => _errors;

    public T? Run<T>(string probe, Func<T?> action)
    {
        try { return action(); }
        catch (Exception error) when (error is not OperationCanceledException) { Record(probe, error); return default; }
    }

    public async Task<T?> RunAsync<T>(string probe, Func<Task<T?>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { Record(probe, error); return default; }
    }

    private void Record(string probe, Exception error)
    {
        var message = error.Message.Length > MaxMessageLength ? error.Message[..MaxMessageLength] : error.Message;
        _errors.Add(new ProbeError(probe, error.GetType().Name, message));
    }
}
