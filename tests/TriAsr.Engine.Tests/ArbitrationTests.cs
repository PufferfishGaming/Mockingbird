using System.Diagnostics;
using System.IO;
using System.Text;
using TriAsr.Application;
using TriAsr.Engine.Llm;

namespace TriAsr.Engine.Tests;

public sealed class ArbitrationTests
{
    private static ChoiceProbabilities P(double a, double b, double unsure = 0) => new(a, b, unsure);

    // ---- the decision rule (pure) --------------------------------------------------------------------------------------------------

    [Fact]
    public void CanaryIsChosenOnlyWhenBothOrdersAgreeAndAreSure()
    {
        // Order 1: A = Whisper, B = Canary, the model says B. Order 2: A = Canary, the model says A.
        var decision = ArbitrationScoring.Decide("a cat", "a dog", P(0.02, 0.97), P(0.95, 0.03));
        Assert.Equal(("canary", "a dog", false), (decision.Choice, decision.Text, decision.Uncertain));
        Assert.Equal(0.96, decision.Confidence, 2);
    }

    [Fact]
    public void ALeaningTowardsTheFirstOptionCancelsOut()
    {
        // A model that says "A" whatever the order is not judging anything: neither side wins.
        var decision = ArbitrationScoring.Decide("a cat", "a dog", P(0.99, 0.01), P(0.99, 0.01));
        Assert.Equal(("uncertain", "a cat", true), (decision.Choice, decision.Text, decision.Uncertain));
    }

    [Fact]
    public void WhisperIsKeptAsResolvedWhenTheModelIsSure()
    {
        var decision = ArbitrationScoring.Decide("a cat", "a dog", P(0.92, 0.05, 0.03), P(0.04, 0.9, 0.06));
        Assert.Equal(("whisper", "a cat", false), (decision.Choice, decision.Text, decision.Uncertain));
    }

    [Fact]
    public void ModerateConfidenceForCanaryIsNotEnoughToChangeWhisper()
    {
        // 0.8 for Canary: measured on a song, the words changed at this level were mostly mistakes. Whisper's wording stays, marked for listening.
        var decision = ArbitrationScoring.Decide("a cat", "a dog", P(0.15, 0.8, 0.05), P(0.8, 0.15, 0.05));
        Assert.Equal(("uncertain", "a cat", true), (decision.Choice, decision.Text, decision.Uncertain));
    }

    [Theory]
    [InlineData(0.9, "canary")]
    [InlineData(0.899, "uncertain")]
    public void TheCanaryThresholdIsExact(double probability, string expected)
        => Assert.Equal(expected, ArbitrationScoring.Decide("x y", "x z", P(1 - probability, probability), P(probability, 1 - probability)).Choice);

    [Fact]
    public void AnEmptySideIsAlwaysUncertainWhateverTheModelSays()
    {
        // Text alone cannot prove that words only one engine wrote were spoken (e.g. an invented "Subtitles 2020").
        var decision = ArbitrationScoring.Decide("Subtitles 2020", "", P(0.99, 0.01), P(0.01, 0.99));
        Assert.True(decision.Uncertain);
        Assert.Equal("Subtitles 2020", decision.Text);
        Assert.True(ArbitrationScoring.Decide("", "added words", P(0, 1), P(1, 0)).Uncertain);
    }

    [Fact]
    public void WhateverTheModelSaysTheTextIsOneOfTheTwoCandidatesExactly()
    {
        var random = new Random(9);
        for (var round = 0; round < 500; round++)
        {
            ChoiceProbabilities Random3() { var a = random.NextDouble(); var b = random.NextDouble(); var u = random.NextDouble(); var s = a + b + u; return new(a / s, b / s, u / s); }
            var decision = ArbitrationScoring.Decide("Well, yes,", "all of this", Random3(), Random3());
            Assert.Contains(decision.Choice, new[] { "whisper", "canary", "uncertain" });
            Assert.Contains(decision.Text, new[] { "Well, yes,", "all of this" });
            Assert.InRange(decision.Confidence, 0, 1);
            Assert.Equal(decision.Choice == "uncertain", decision.Uncertain);
            if (decision.Choice == "uncertain") Assert.Equal("Well, yes,", decision.Text);   // doubt always leaves Whisper's wording
        }
    }

    // ---- reading the server's answer -----------------------------------------------------------------------------------------------

    private static string Answer(params (string Token, double LogProb)[] top) =>
        "{\"choices\":[{\"message\":{\"content\":\"A\"},\"logprobs\":{\"content\":[{\"token\":\"A\",\"logprob\":-0.1,\"top_logprobs\":["
        + string.Join(",", top.Select(item => $"{{\"token\":\"{item.Token}\",\"logprob\":{item.LogProb.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")) + "]}]}}]}";

    [Fact]
    public void TheThreeLettersAreReadAndNormalisedAndOtherTokensIgnored()
    {
        var p = ArbitrationScoring.ReadProbabilities(Answer(("A", Math.Log(0.6)), ("B", Math.Log(0.2)), ("U", Math.Log(0.1)), ("The", Math.Log(0.05)), ("I", Math.Log(0.05))));
        Assert.Equal(0.6 / 0.9, p.A, 9); Assert.Equal(0.2 / 0.9, p.B, 9); Assert.Equal(0.1 / 0.9, p.Unsure, 9);
        Assert.Equal(1.0, p.A + p.B + p.Unsure, 9);
    }

    [Fact]
    public void ALetterThatAppearsInAnotherFormIsStillCounted()
    {
        var p = ArbitrationScoring.ReadProbabilities(Answer(("A", Math.Log(0.5)), (" a", Math.Log(0.25)), ("b", Math.Log(0.25))));
        Assert.Equal((0.75, 0.25, 0.0), (p.A, p.B, p.Unsure));
    }

    [Fact]
    public void AnAnswerWithNoneOfTheLettersMeansTheModelCouldNotTell()
        => Assert.Equal(ChoiceProbabilities.None, ArbitrationScoring.ReadProbabilities(Answer(("The", -0.2), ("Sure", -1.5))));

    [Fact]
    public void AResponseWithoutProbabilitiesIsAnErrorNotAGuess()
    {
        Assert.Throws<InvalidDataException>(() => ArbitrationScoring.ReadProbabilities("{\"choices\":[{\"message\":{\"content\":\"A\"}}]}"));
        Assert.Throws<InvalidDataException>(() => ArbitrationScoring.ReadProbabilities("{\"choices\":[]}"));
    }

    // ---- the real model: a fake server cannot know what the model really answers -----------------------------------------------------

    private static (string Server, string Model)? FindModel()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var server = Path.Combine(directory.FullName, "Runtimes", "Llama", "llama-server.exe");
            var folder = Path.Combine(directory.FullName, "Models", "Correction");
            if (File.Exists(server) && Directory.Exists(folder))
            {
                var model = Directory.GetFiles(folder, "*.gguf").OrderBy(path => new FileInfo(path).Length).FirstOrDefault();
                if (model is not null) return (server, model);
            }
        }
        return null;
    }

    private sealed class CorrectionModelFactAttribute : FactAttribute
    {
        public CorrectionModelFactAttribute() { if (FindModel() is null) Skip = "No correction model found: pending on a machine with Runtimes/Llama and Models/Correction."; }
    }

    private sealed class StreamingRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var info = new ProcessStartInfo(request.Executable) { WorkingDirectory = request.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            using var stop = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = new StringBuilder();
            var reading = Task.Run(async () => { string? line; while ((line = await process.StandardError.ReadLineAsync()) is not null) { errors.AppendLine(line); request.ErrorLine?.Invoke(line); } });
            await process.WaitForExitAsync(CancellationToken.None);
            await reading;
            return new(process.ExitCode, await output, errors.ToString(), 0);
        }
    }

    [CorrectionModelFact]
    public async Task TheRealModelAnswersCandidatesWithPunctuationWithoutBeingRejected()
    {
        var (server, model) = FindModel()!.Value;
        await using var arbiter = new LlamaArbiter(new StreamingRunner(), server, model, "cpu", 4);
        await arbiter.StartAsync(default);
        // The earlier design rejected most of these: the model wrote the whole sentence where only the candidate was allowed.
        var cases = new[]
        {
            ("Well, yes,", "all of this", "She looked at him and said", "and then she left the room"),
            ("'Cause", "Because", "", "it was raining outside"),
            ("Schulternhalle", "Schulturnhalle", "Ich habe gesehen, dass ein Verein unsere ", " benutzt."),
        };
        foreach (var (whisper, canary, before, after) in cases)
        {
            var decision = await arbiter.ResolveAsync(whisper, canary, before, after, default);
            Assert.Contains(decision.Choice, new[] { "whisper", "canary", "uncertain" });
            Assert.Contains(decision.Text, new[] { whisper, canary });
            Assert.InRange(decision.Confidence, 0, 1);
        }
        Assert.Equal("cpu", arbiter.ActualBackend);
        Assert.True(arbiter.LastTokensPerSecond > 0);
    }
}
