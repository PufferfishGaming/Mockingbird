namespace TriAsr.Application;

/// <summary>
/// The sentences the link code says to a person, in English. They come from layers that cannot translate; the app (and the web page) look them up by their
/// wording, so each of them is also listed in <c>extra-keys.json</c> and has a translation (ADR-0018).
/// </summary>
public static class LinkMessages
{
    public const string Paste = "Paste the address of a video or an audio file.";
    public const string NotAddress = "That is not a web address.";
    public const string OnlyWeb = "Only web addresses (http or https) can be used as links.";
    public const string HasUserInfo = "An address with a user name or password in it cannot be used. Paste the plain address.";
    public const string PrivateNetwork = "Links to addresses inside a private network are not followed.";
    public const string Unreachable = "That address could not be reached.";
    public const string TooManyRedirects = "That address redirects too often.";
    public const string TooLarge = "The recording is larger than the limit for links.";
    public const string Interrupted = "The download was interrupted before the end.";
    public const string NeedsHelper = "That address leads to a web page, not to an audio or video file. To fetch the sound of pages, install the link helper first.";
    public const string NoSound = "No sound was found at that address.";
    public const string NeedsLogin = "That recording needs a login or is private. Only public recordings can be fetched.";
    public const string Failed = "The link could not be downloaded.";
    /// <summary>The start of "The link could not be downloaded: {reason}", where the reason is the site's own words.</summary>
    public const string FailedPrefix = "The link could not be downloaded: ";
    public const string HelperDownload = "The link helper could not be downloaded.";
    public const string HelperChecksum = "The link helper failed its checksum and was not installed.";
    public const string HelperInUse = "The link helper is in use. Wait until the download that uses it has finished, then try again.";

    /// <summary>The sentences that have no facts in them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Paste, NotAddress, OnlyWeb, HasUserInfo, PrivateNetwork, Unreachable, TooManyRedirects, TooLarge, Interrupted, NeedsHelper, NoSound, NeedsLogin, Failed,
        HelperDownload, HelperChecksum, HelperInUse
    ];
}

/// <summary>A recording that was downloaded from a link: the file, and the title the site gave it.</summary>
public sealed record FetchedLink(string Path, string Title);

/// <summary>The link could not be used or downloaded. The message says why, in plain words (it comes from the layers below the interface and is in English).</summary>
public sealed class LinkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a link is fetched: where it may lead, and how much may be taken.</summary>
/// <param name="AllowPrivateNetwork">True for a link a person pasted on their own computer. A server that fetches for other computers leaves it false (ADR-0018).</param>
/// <param name="MaxBytes">The most that may be downloaded. Zero means the default (4 GB).</param>
public sealed record LinkFetchOptions(bool AllowPrivateNetwork = false, long MaxBytes = 0)
{
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;
    public long Limit => MaxBytes > 0 ? MaxBytes : DefaultMaxBytes;
}

/// <summary>
/// Downloads the sound of a web address so that it can be transcribed like any other recording (ADR-0018). A link straight to an audio or video file
/// is downloaded as it is; a link to a page (a video on one of the many sites that host them, a podcast episode) needs the helper program, see <see cref="ILinkTool"/>.
/// </summary>
public interface ILinkFetcher
{
    /// <summary>Whether links to pages (not only to files) can be fetched, which is when the helper program is installed.</summary>
    bool PagesReady { get; }

    /// <summary>Whether the address may be fetched at all: a server checks this at once so that a client is told before a job exists. It resolves the name of the host.</summary>
    /// <exception cref="LinkException">The address leads into a private network (and that is not allowed), or cannot be found.</exception>
    Task CheckAsync(Uri link, bool allowPrivateNetwork, CancellationToken token);

    /// <param name="link">An http or https address (<c>LinkPolicy.Parse</c>).</param>
    /// <param name="folder">Where the file goes. The folder is created.</param>
    /// <param name="percent">How much has been downloaded, 0 to 100, as far as the site says.</param>
    /// <exception cref="LinkException">The link is not allowed, cannot be reached, or has no sound in it.</exception>
    Task<FetchedLink> FetchAsync(Uri link, string folder, LinkFetchOptions options, IProgress<double>? percent, CancellationToken token);
}

/// <summary>The helper program that fetches pages (yt-dlp). It is not part of the installer: it is downloaded once, from its own project, when the user asks for it.</summary>
public interface ILinkTool
{
    bool Installed { get; }

    /// <summary>Downloads the program (or its newer version) and checks it against the checksum its project publishes. Does nothing when the installed one is the newest.</summary>
    /// <returns>True when a new copy was put in place.</returns>
    Task<bool> InstallAsync(IProgress<double>? percent, CancellationToken token);

    /// <summary>Whether the project publishes a newer version than the one installed. False when it is not installed.</summary>
    Task<bool> UpdateAvailableAsync(CancellationToken token);
}
