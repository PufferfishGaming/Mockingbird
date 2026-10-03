using System.IO;
using System.Text.RegularExpressions;
using TriAsr.Audio;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// The FFmpeg that Studio and Server ship is Mockingbird's own build (scripts/build-ffmpeg.ps1): LGPL, from a pinned official source release,
/// with the encoders and muxers the program uses, and its source bundle goes on the release page with the installers.
/// </summary>
public sealed class FfmpegBuildTests
{
    private static string Script(string path) => File.ReadAllText(Path.Combine(TranslationSources.RepositoryRoot(), "scripts", path));

    private static string Configure() => string.Join(" ", Script("ffmpeg/build.sh").Split('\n').SkipWhile(line => !line.StartsWith("set -- ")).TakeWhile(line => !line.StartsWith("./configure")));

    [Fact]
    public void TheBuildStaysLgplAndRefusesToFinishOtherwise()
    {
        var configure = Configure();
        Assert.DoesNotContain("--enable-gpl", configure);
        Assert.DoesNotContain("--enable-nonfree", configure);
        Assert.DoesNotContain("--enable-version3", configure);
        Assert.Contains("--disable-autodetect", configure);                     // no library of the build machine slips in
        Assert.Contains("grep -q '^License: LGPL version 2.1 or later'", Script("ffmpeg/build.sh"));
    }

    [Fact]
    public void TheSourceAndTheBuildMachineArePinned()
    {
        Assert.Matches(new Regex(@"\$Sha256 = '[0-9A-F]{64}'"), Script("build-ffmpeg.ps1"));
        Assert.Matches(new Regex(@"^FROM debian:[\w.-]+@sha256:[0-9a-f]{64}$", RegexOptions.Multiline), Script("ffmpeg/Dockerfile").Replace("\r", ""));
        Assert.Contains("ucrt", Script("ffmpeg/Dockerfile"));                      // the C runtime the earlier builds used, so the audio stays the same to the bit
    }

    [Fact]
    public void WhatTheProgramAsksOfFfmpegIsBuiltIn()
    {
        var configure = Configure();
        string[] Enabled(string kind) => Regex.Match(configure, $@"--enable-{kind}=([\w,]+)").Groups[1].Value.Split(',');
        var playback = FfmpegNormalizer.PlaybackArguments("in.mp4", "out.m4a");
        Assert.Contains(playback[Array.IndexOf(playback, "-c:a") + 1], Enabled("encoder"));
        Assert.Contains("pcm_s16le", Enabled("encoder"));                         // normalized.wav and every cut of it
        Assert.Contains("ipod", Enabled("muxer"));                                // .m4a
        Assert.Contains("wav", Enabled("muxer"));
        Assert.Contains("file", Enabled("protocol"));
        Assert.DoesNotContain("--disable-decoders", configure);                   // a recording may arrive in any format
        Assert.DoesNotContain("--disable-demuxers", configure);
        Assert.DoesNotContain("--disable-filters", configure);
    }

    [Fact]
    public void TheSourceBundleTravelsWithEveryPackageThatCarriesFfmpeg()
    {
        Assert.Contains("FFmpeg-$ffmpegVersion-source.zip", Script("package.ps1"));
        Assert.Contains("is not the program BUILD-INFO.txt describes", Script("package.ps1"));
        Assert.Contains("FFmpeg source bundle beside the installer", Script("release-audit.ps1"));
        Assert.Contains("FFmpeg-*-source.zip", Script("build-setup.ps1"));        // listed in the SHA256SUMS file
        Assert.Contains("FFmpeg-*-source.zip", Script("release-chain.ps1"));      // and in the upload list
        var attributes = File.ReadAllText(Path.Combine(TranslationSources.RepositoryRoot(), ".gitattributes"));
        Assert.Contains("*.sh text eol=lf", attributes);                          // build.sh runs in Linux
    }
}
