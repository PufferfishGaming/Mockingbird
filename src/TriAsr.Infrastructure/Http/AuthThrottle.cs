using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TriAsr.Infrastructure;

/// <summary>
/// Slows down guessing of the API key: after <paramref name="maxFailures"/> wrong keys from one address within <paramref name="window"/>, that address
/// is refused for <paramref name="lockout"/>, even with the right key. Memory is bounded: old entries are dropped as new ones arrive.
/// </summary>
public sealed class AuthThrottle(int maxFailures = 8, TimeSpan? window = null, TimeSpan? lockout = null, Func<DateTimeOffset>? clock = null)
{
    private sealed record Entry(int Failures, DateTimeOffset First, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _lockout = lockout ?? TimeSpan.FromMinutes(1);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>True while the address is locked out.</summary>
    public bool IsBlocked(string address) =>
        _entries.TryGetValue(address, out var entry) && entry.LockedUntil is { } until && until > _now();

    public void RecordFailure(string address)
    {
        var now = _now();
        if (_entries.Count > 4096) foreach (var (key, entry) in _entries) if (Expired(entry, now)) _entries.TryRemove(key, out _);
        _entries.AddOrUpdate(address,
            _ => new Entry(1, now, maxFailures <= 1 ? now + _lockout : null),
            (_, entry) =>
            {
                if (Expired(entry, now)) return new Entry(1, now, maxFailures <= 1 ? now + _lockout : null);
                var failures = entry.Failures + 1;
                return entry with { Failures = failures, LockedUntil = failures >= maxFailures ? now + _lockout : entry.LockedUntil };
            });
    }

    public void RecordSuccess(string address) => _entries.TryRemove(address, out _);

    private bool Expired(Entry entry, DateTimeOffset now) => entry.LockedUntil is { } until ? until <= now : now - entry.First > _window;

    /// <summary>Compares two secrets in a time that does not depend on how many leading characters match.</summary>
    public static bool SecretsEqual(string? given, string expected)
    {
        if (given is null || expected.Length == 0) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>A new key: 32 random bytes, written with letters and digits only so that it can be typed or pasted anywhere.</summary>
    public static string NewKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var key = new StringBuilder("mbk-");
        for (var i = 0; i < 40; i++) key.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        return key.ToString();
    }
}
