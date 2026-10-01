using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class ModelDownloadTests
{
    [Fact]
    public async Task ResumesExactRangeAndVerifiesBeforePublishing()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8];
            var entry = new ModelEntry("fixture", "test", "test", "test", "test", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "https://fixture.invalid/model", "model.gguf", "test");
            await File.WriteAllBytesAsync(Path.Combine(root, "model.gguf.partial"), bytes[..4]);
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.Equal(4, request.Headers.Range!.Ranges.Single().From);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[4..]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 7, 8); return response;
            }));
            using var store = new ModelStore(root, http);
            await store.DownloadAsync(entry, null);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(store.PathFor(entry))); Assert.True(await store.VerifyCachedAsync(entry));
            await File.WriteAllBytesAsync(store.PathFor(entry), new byte[8]); File.SetLastWriteTimeUtc(store.PathFor(entry), DateTime.UtcNow.AddSeconds(2));
            Assert.False(await store.VerifyCachedAsync(entry));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(entry, null));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ManifestPathCannotEscapeItsRoot()
    {
        using var store = new ModelStore(Path.GetTempPath());
        var entry = ModelManifest.Entries[0] with { RelativePath = "../escape.bin" };
        Assert.Throws<InvalidDataException>(() => store.PathFor(entry));
    }
    [Fact]
    public async Task DownloadAndVerificationSurviveReloadWithoutNetworkAndChangedFilesInvalidateReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = [9, 8, 7, 6];
            var entry = new ModelEntry("fixture", "test", "test", "test", "test", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "https://fixture.invalid/model", "model.gguf", "test");
            using (var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            using (var store = new ModelStore(root, http)) await store.DownloadAsync(entry, null);
            using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("Network must not be used for a saved model.")));
            using var reloaded = new ModelStore(root, offline);
            Assert.True(reloaded.Inspect(entry).Verified);
            await reloaded.DownloadAsync(entry, null);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(reloaded.PathFor(entry)));
            Assert.False(reloaded.Inspect(entry with { Sha256 = new string('0', 64) }).Verified);
            await File.WriteAllBytesAsync(reloaded.PathFor(entry), new byte[4]);
            File.SetLastWriteTimeUtc(reloaded.PathFor(entry), DateTime.UtcNow.AddSeconds(5));
            Assert.False(reloaded.Inspect(entry).Verified);
            Assert.False(await reloaded.VerifyCachedAsync(entry));
            Assert.False(File.Exists(reloaded.PathFor(entry) + ".verified.json"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task PartialDownloadIsRecognizedByANewStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var entry = ModelManifest.Entries[0] with { RelativePath = "paused.bin" };
            await File.WriteAllBytesAsync(Path.Combine(root, "paused.bin.partial"), [1, 2, 3]);
            using var store = new ModelStore(root);
            Assert.Equal(3, store.Inspect(entry).PartialBytes);
            Assert.False(store.Inspect(entry).Installed);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
