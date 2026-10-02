using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TriAsr.Infrastructure;

/// <summary>What a hosted server announces about itself on the local network. The sender's address is taken from the packet, never from this text.</summary>
/// <param name="Id">Stays the same for a server across restarts (kept with its identity), so that a renamed server is still the same server.</param>
/// <param name="Fingerprint">The certificate the server will present. Anyone on the network can send a beacon, so a client trusts the fingerprint only after the user has seen it match.</param>
public sealed record ServerAnnouncement(string Id, string Name, int Port, bool Tls, bool PasswordRequired, string Edition, string Version, string Fingerprint);

/// <summary>A server heard on the network.</summary>
public sealed record DiscoveredServer(ServerAnnouncement Announcement, IPAddress Address, DateTimeOffset LastSeen, bool ThisComputer)
{
    public string Id => Announcement.Id;
    /// <summary>Where to connect.</summary>
    public Uri Uri => new($"{(Announcement.Tls ? "https" : "http")}://{(Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString())}:{Announcement.Port}");
}

/// <summary>The packet a server broadcasts, and the checks a receiver makes before believing any of it.</summary>
public static class ServerBeacon
{
    /// <summary>UDP port beacons are sent to and heard on.</summary>
    public const int DefaultPort = 8643;
    public const string Magic = "mockingbird-server";
    private const int MaxPacket = 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Packet(string Magic, int V, ServerAnnouncement Server);

    public static byte[] Encode(ServerAnnouncement announcement) =>
        JsonSerializer.SerializeToUtf8Bytes(new Packet(Magic, 1, Clean(announcement)), Json);

    /// <summary>The announcement in a packet, or null when it is not one (wrong magic, too long, impossible values). Names are shortened and stripped of control characters.</summary>
    public static ServerAnnouncement? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxPacket) return null;
        try
        {
            var packet = JsonSerializer.Deserialize<Packet>(data, Json);
            if (packet is not { Server: { } server } || packet.Magic != Magic || packet.V != 1) return null;
            if (server.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 64) return null;
            return Clean(server);
        }
        catch (JsonException) { return null; }
    }

    private static string Text(string? value, int max) =>
        new(string.Concat((value ?? "").Where(c => !char.IsControl(c))).Trim().Take(max).ToArray());

    private static ServerAnnouncement Clean(ServerAnnouncement server) => server with
    {
        Id = Text(server.Id, 64), Name = Text(server.Name, 60), Edition = Text(server.Edition, 16), Version = Text(server.Version, 24),
        Fingerprint = new string(server.Fingerprint.Where(char.IsAsciiHexDigit).Take(64).ToArray())
    };

    /// <summary>The addresses beacons are sent to: the broadcast address of each network the computer is on, and the computer itself.</summary>
    public static IReadOnlyList<IPEndPoint> DefaultTargets(int port)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Loopback, port) };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.IPv4Mask is not null))
                {
                    var address = unicast.Address.GetAddressBytes(); var mask = unicast.IPv4Mask!.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                    var endpoint = new IPEndPoint(new IPAddress(broadcast), port);
                    if (!targets.Contains(endpoint)) targets.Add(endpoint);
                }
        }
        catch (NetworkInformationException) { }
        return targets;
    }

    /// <summary>The addresses this computer has, to tell its own beacons from those of others.</summary>
    public static HashSet<IPAddress> LocalAddresses()
    {
        var own = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try { foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()) foreach (var unicast in adapter.GetIPProperties().UnicastAddresses) own.Add(unicast.Address); }
        catch (NetworkInformationException) { }
        return own;
    }
}

/// <summary>Announces a hosted server every few seconds while there is something to announce.</summary>
public sealed class BeaconSender : IAsyncDisposable
{
    private readonly Func<ServerAnnouncement?> _source;
    private readonly int _port;
    private readonly TimeSpan _interval;
    private readonly Func<IEnumerable<IPEndPoint>> _targets;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public BeaconSender(Func<ServerAnnouncement?> source, int port = ServerBeacon.DefaultPort, TimeSpan? interval = null, Func<IEnumerable<IPEndPoint>>? targets = null)
    {
        _source = source; _port = port; _interval = interval ?? TimeSpan.FromSeconds(2);
        _targets = targets ?? (() => ServerBeacon.DefaultTargets(port));
    }

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        using var socket = new UdpClient { EnableBroadcast = true };
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_source() is { } announcement)
                {
                    var packet = ServerBeacon.Encode(announcement);
                    foreach (var target in _targets())
                    {
                        try { await socket.SendAsync(packet, target, _stop.Token); }
                        catch (SocketException) { } // a network that is gone, or one that does not allow broadcasts
                    }
                }
                await Task.Delay(_interval, _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { } }
        _stop.Dispose();
    }
}

/// <summary>Listens for beacons and keeps the list of servers that were heard recently.</summary>
public sealed class BeaconListener : IAsyncDisposable
{
    private const int MaxServers = 100;
    private readonly int _port;
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DiscoveredServer> _servers = [];
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<IPAddress> _own = ServerBeacon.LocalAddresses();
    private UdpClient? _socket;
    private Task? _receiving, _pruning;

    public BeaconListener(int port = ServerBeacon.DefaultPort, TimeSpan? lifetime = null, Func<DateTimeOffset>? clock = null)
    {
        _port = port; _lifetime = lifetime ?? TimeSpan.FromSeconds(8); _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised (on a background thread) when a server appears, changes or goes away.</summary>
    public event Action? Changed;

    /// <summary>The servers heard in the last few seconds, by name.</summary>
    public IReadOnlyList<DiscoveredServer> Servers
    {
        get
        {
            lock (_sync) { Expire(); return _servers.Values.OrderBy(server => server.Announcement.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(server => server.Id).ToArray(); }
        }
    }

    /// <summary>Starts listening. False when the port cannot be used (then there is simply nothing to discover; servers can still be added by address).</summary>
    public bool Start()
    {
        try
        {
            var socket = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            _socket = socket;
        }
        catch (SocketException) { return false; }
        _receiving = Task.Run(ReceiveAsync);
        _pruning = Task.Run(PruneAsync);
        return true;
    }

    private async Task ReceiveAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var received = await _socket!.ReceiveAsync(_stop.Token);
                if (ServerBeacon.Decode(received.Buffer) is not { } announcement) continue;
                var server = new DiscoveredServer(announcement, received.RemoteEndPoint.Address, _now(), _own.Contains(received.RemoteEndPoint.Address));
                bool changed;
                lock (_sync)
                {
                    changed = !_servers.TryGetValue(announcement.Id, out var before) || before.Announcement != announcement || !before.Address.Equals(server.Address);
                    if (changed && !_servers.ContainsKey(announcement.Id) && _servers.Count >= MaxServers) continue; // a flood of made-up servers fills no memory
                    _servers[announcement.Id] = server;
                }
                if (changed) Changed?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_stop.IsCancellationRequested) return; await Task.Delay(500); }
        }
    }

    private async Task PruneAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token); } catch (OperationCanceledException) { return; }
            bool changed;
            lock (_sync) changed = Expire();
            if (changed) Changed?.Invoke();
        }
    }

    private bool Expire()
    {
        var cutoff = _now() - _lifetime;
        var gone = _servers.Where(pair => pair.Value.LastSeen < cutoff).Select(pair => pair.Key).ToArray();
        foreach (var id in gone) _servers.Remove(id);
        return gone.Length > 0;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket?.Dispose();
        foreach (var task in new[] { _receiving, _pruning }) if (task is not null) { try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { } }
        _stop.Dispose();
    }
}
