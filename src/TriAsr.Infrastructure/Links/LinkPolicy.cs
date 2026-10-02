using System.Net;
using System.Net.Sockets;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>
/// What may be fetched as a link (ADR-0018). A link is an http or https address of a page on the web. When the program fetches it on behalf of somebody
/// else (a server for the computers that use it) it must not be made to reach into the network it sits in, so such links have to lead to public
/// addresses; on the user's own computer, for a link the user pasted, that rule is not applied.
/// </summary>
public static class LinkPolicy
{
    public const int MaxLength = 2000;

    /// <summary>
    /// Reads what a person pasted: a web address with or without <c>https://</c>. Anything else (another kind of address, an address with a user name
    /// and password in it, a very long text) is refused with a reason.
    /// </summary>
    public static Uri Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) throw new LinkException(LinkMessages.Paste);
        if (trimmed.Length > MaxLength) throw new LinkException(LinkMessages.NotAddress);
        if (trimmed.Any(char.IsControl) || trimmed.Any(char.IsWhiteSpace)) throw new LinkException(LinkMessages.NotAddress);
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var link)) throw new LinkException(LinkMessages.NotAddress);
        if (link.Scheme is not ("http" or "https")) throw new LinkException(LinkMessages.OnlyWeb);
        if (link.Host.Length == 0) throw new LinkException(LinkMessages.NotAddress);
        if (link.UserInfo.Length > 0) throw new LinkException(LinkMessages.HasUserInfo);
        return link;
    }

    /// <summary>Refuses a link whose host is, or resolves to, an address inside a private network, unless that is allowed.</summary>
    public static async Task EnsureAllowedAsync(Uri link, bool allowPrivateNetwork, CancellationToken token)
    {
        if (allowPrivateNetwork) return;
        const string refusal = LinkMessages.PrivateNetwork;
        var host = link.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) throw new LinkException(refusal);
        if (IPAddress.TryParse(host, out var literal)) { if (!IsPublic(literal)) throw new LinkException(refusal); return; }
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false); }
        catch (SocketException) { throw new LinkException(LinkMessages.Unreachable); }
        if (addresses.Length == 0) throw new LinkException(LinkMessages.Unreachable);
        if (addresses.Any(address => !IsPublic(address))) throw new LinkException(refusal);
    }

    /// <summary>Whether an address belongs to the public internet: not this computer, not a private, shared, link-local, multicast or reserved range.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127           // shared address space of carriers
                || bytes[0] == 169 && bytes[1] == 254                         // link-local
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0           // protocol assignments
                || bytes[0] == 198 && bytes[1] is 18 or 19                     // benchmarking
                || bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 || bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 || bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)) return false;
        if (bytes[..12].All(b => b == 0)) return IsPublic(new IPAddress(bytes[12..16]));              // ::a.b.c.d, the old way of writing an IPv4 address
        if (bytes[0] == 0xff) return false;                                    // multicast
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return false;       // link-local
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) return false;       // site-local (retired, still private)
        if ((bytes[0] & 0xfe) == 0xfc) return false;                           // unique local
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return false; // documentation
        if (bytes[0] == 0 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)                    // 64:ff9b::/96 translates to an IPv4 address
            return IsPublic(new IPAddress(bytes[12..16]));
        return true;
    }
}
