using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>
/// Fetches a link: it must be a public web address (unless the person pasted it on their own computer), a link straight to an audio or
/// video file is downloaded as it is, and a link to a page is handed to the helper program when that is installed.
/// </summary>
public sealed class LinkFetcher(DirectLinkDownloader direct, YtDlpPageFetcher pages, YtDlpTool tool) : ILinkFetcher
{
    public bool PagesReady => tool.Installed;

    public Task CheckAsync(Uri link, bool allowPrivateNetwork, CancellationToken token) => LinkPolicy.EnsureAllowedAsync(link, allowPrivateNetwork, token);

    public async Task<FetchedLink> FetchAsync(Uri link, string folder, LinkFetchOptions options, IProgress<double>? percent, CancellationToken token)
    {
        await CheckAsync(link, options.AllowPrivateNetwork, token).ConfigureAwait(false);
        if (await direct.TryAsync(link, folder, options, percent, token).ConfigureAwait(false) is { } file) return file;
        if (!PagesReady) throw new LinkException(LinkMessages.NeedsHelper);
        return await pages.FetchAsync(link, folder, options, percent, token).ConfigureAwait(false);
    }
}
