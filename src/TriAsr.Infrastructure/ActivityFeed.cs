using Serilog.Core;
using Serilog.Events;

namespace TriAsr.Infrastructure;

public sealed record ActivityEntry(long Sequence, DateTimeOffset At, string Category, string Message)
{
    public string Display => $"{At:HH:mm:ss.fff} [{Category}] {Message}";
}

public sealed class ActivityFeed : ILogEventSink
{
    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _entries = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private long _sequence;
    public const int Capacity = 2000;
    public long Sequence { get { lock (_gate) return _sequence; } }
    public void RegisterSecret(string secret) { if (!string.IsNullOrEmpty(secret)) lock (_gate) _secrets.Add(secret); }
    public void Append(string category, string message)
    {
        lock (_gate)
        {
            foreach (var secret in _secrets) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
            if (message.Length > 4096) message = message[..4096] + " …";
            _entries.Enqueue(new(++_sequence, DateTimeOffset.Now, category, message.TrimEnd('\r', '\n')));
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }
    public IReadOnlyList<ActivityEntry> Snapshot(long after = 0) { lock (_gate) return _entries.Where(entry => entry.Sequence > after).ToArray(); }
    public void Emit(LogEvent logEvent) => Append(logEvent.Level.ToString(), logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) +
        (logEvent.Exception is null ? "" : "\n" + logEvent.Exception));
    public string DescribeCommand(string executable, IReadOnlyList<string> arguments)
    {
        var safe = arguments.ToArray();
        for (var index = 0; index < safe.Length; index++)
            if (safe[index] is "--api-key" or "--password" or "--token" && index + 1 < safe.Length)
            { RegisterSecret(safe[index + 1]); safe[++index] = "[redacted]"; }
        return Path.GetFileName(executable) + " " + string.Join(" ", safe.Select(argument => argument.Contains(' ') ? "\"" + argument + "\"" : argument));
    }
}
