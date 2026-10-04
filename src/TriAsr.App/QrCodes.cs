using System.Net;
using System.Windows;
using System.Windows.Media;
using Net.Codecrete.QrCodeGenerator;

namespace TriAsr.App;

/// <summary>QR codes of a server's address, so that a phone opens its web page by pointing its camera at the screen.</summary>
public static class QrCodes
{
    /// <summary>The light border every reader needs around the code, in modules.</summary>
    public const int QuietZone = 4;

    /// <summary>The modules of the code of <paramref name="text"/>, one string per row from the top: '1' is dark, '0' light. The quiet zone is not included.</summary>
    public static string[] Rows(string text)
    {
        var code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        return Enumerable.Range(0, code.Size)
            .Select(y => new string(Enumerable.Range(0, code.Size).Select(x => code.GetModule(x, y) ? '1' : '0').ToArray())).ToArray();
    }

    /// <summary>The code as a picture: dark modules on white with the quiet zone around them, whatever the theme (readers need dark on light).</summary>
    public static ImageSource Image(string text)
    {
        var rows = Rows(text);
        var side = rows.Length + 2 * QuietZone;
        var dark = new StreamGeometry();
        using (var context = dark.Open())
            for (var y = 0; y < rows.Length; y++)
                for (var x = 0; x < rows.Length;)
                {
                    if (rows[y][x] != '1') { x++; continue; }
                    var start = x;
                    while (x < rows.Length && rows[y][x] == '1') x++;
                    // A run of dark modules in a row is one rectangle.
                    context.BeginFigure(new Point(start + QuietZone, y + QuietZone), true, true);
                    context.PolyLineTo([new Point(x + QuietZone, y + QuietZone), new Point(x + QuietZone, y + QuietZone + 1), new Point(start + QuietZone, y + QuietZone + 1)], false, false);
                }
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, side, side))));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, dark));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The address a phone on the same network opens for a server: the address a request came in on when that is an IP address the network can
    /// reach (a computer's name may not resolve on a phone), else the server's own address on the network. Null when the server is reachable from this computer only.
    /// </summary>
    public static string? PhoneAddress(string? hostHeader, bool secure, string? networkAddress)
    {
        if (networkAddress is null) return null;
        if (!secure || string.IsNullOrWhiteSpace(hostHeader)) return networkAddress;
        var host = hostHeader.Trim();
        var name = host.StartsWith('[') && host.IndexOf(']') is var end and > 0 ? host[1..end]
            : host.IndexOf(':') is var colon and > 0 && colon == host.LastIndexOf(':') ? host[..colon] : host;
        return IPAddress.TryParse(name, out var address) && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal ? "https://" + host : networkAddress;
    }
}
