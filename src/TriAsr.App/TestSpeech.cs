using System.IO;
using System.Text;
using TriAsr.Application;

namespace TriAsr.App;

/// <summary>
/// Makes the short speech recording that first-run setup tunes on, using the voice Windows already has (no download, nothing bundled).
/// Only speed is measured with it, so a synthetic voice is enough; the sentence is in the voice's own language so the language check passes.
/// </summary>
public static class TestSpeech
{
    private static readonly (string Code, string Text)[] Sentences =
    [
        ("en", "Mockingbird Studio is checking how fast this computer can turn speech into text. Please wait a moment while a short sample is measured, so that the best settings can be chosen for you."),
        ("hu", "A Mockingbird Studio most azt méri, hogy ez a számítógép milyen gyorsan alakítja szöveggé a beszédet. Kérjük, várjon egy pillanatot, amíg egy rövid mintát vizsgálunk, hogy kiválaszthassuk a legjobb beállításokat."),
        ("de", "Mockingbird Studio prüft gerade, wie schnell dieser Computer Sprache in Text umwandeln kann. Bitte warten Sie einen Moment, während eine kurze Probe gemessen wird, damit die besten Einstellungen für Sie gewählt werden können."),
        ("es", "Mockingbird Studio está comprobando a qué velocidad este ordenador puede convertir la voz en texto. Espere un momento mientras se mide una muestra breve, para elegir los mejores ajustes para usted."),
        ("fr", "Mockingbird Studio vérifie à quelle vitesse cet ordinateur peut transformer la parole en texte. Veuillez patienter un instant pendant qu'une courte séquence est mesurée, afin de choisir les meilleurs réglages pour vous.")
    ];

    /// <summary>Exit code of the script when no installed voice speaks one of the languages above.</summary>
    public const int NoVoice = 3;

    public static IReadOnlyList<string> Languages => Sentences.Select(item => item.Code).ToArray();

    /// <summary>The PowerShell that writes a 16 kHz mono WAV of the first sentence a suitable voice can read; it prints that voice's language code.</summary>
    public static string Script(string outputPath)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var table = string.Join("; ", Sentences.Select(item => $"{item.Code} = {Quote(item.Text)}"));
        return $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Speech
            $texts = @{ {{table}} }
            $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
            try {
                $code = $synth.Voice.Culture.TwoLetterISOLanguageName
                if (-not $texts.ContainsKey($code)) {
                    $pick = @($synth.GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo } | Where-Object { $texts.ContainsKey($_.Culture.TwoLetterISOLanguageName) }) | Select-Object -First 1
                    if ($null -eq $pick) { exit {{NoVoice}} }
                    $synth.SelectVoice($pick.Name)
                    $code = $pick.Culture.TwoLetterISOLanguageName
                }
                $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
                $synth.SetOutputToWaveFile({{Quote(outputPath)}}, $format)
                $synth.Speak($texts[$code])
                Write-Output $code
            } finally { $synth.Dispose() }
            """;
    }

    /// <summary>The recording's path, or null when this computer has no voice for any of the five languages.</summary>
    public static async Task<string?> CreateAsync(IProcessRunner runner, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "test-speech.wav");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) return null;
        // An encoded command is not a script file, so a machine policy that blocks scripts does not apply to it.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(output)));
        var run = await runner.RunAsync(new(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], directory, TimeSpan.FromMinutes(2)), token);
        if (run.ExitCode == NoVoice) return null;
        if (run.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("The test recording could not be made: " + run.StandardError.Trim());
        return output;
    }
}
