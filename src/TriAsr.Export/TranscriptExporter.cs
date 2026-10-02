using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TriAsr.Domain;

namespace TriAsr.Export;

public static class TranscriptExporter
{
    public static FinalTranscript ReadableCopy(FinalTranscript transcript) => transcript with
    {
        Regions = transcript.Regions.Select(region => region with
        {
            FinalText = System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(region.FinalText, @"\s+", " ").Trim(), @"\s+([,.;:!?])", "$1"),
            Source = "readable-derived/" + region.Source
        }).ToArray()
    };
    public static string Timestamp(long milliseconds, char separator = '.')
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}{separator}{milliseconds % 1000:000}");
    }
    public static async Task SaveAsync(FinalTranscript transcript, string path, CancellationToken token = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".srt" or ".vtt" && transcript.Regions.Any(region => !region.NativeTimestamps))
            throw new InvalidDataException("Subtitle export requires native timestamps. This Canary-only transcript can be exported as text, Markdown, JSON, CSV or DOCX.");
        foreach (var region in transcript.Regions)
            if (region.StartMs < 0 || region.EndMs < region.StartMs) throw new InvalidDataException("Invalid transcript timing.");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (extension == ".docx") WriteDocx(transcript, temporary);
            else
            {
                var text = extension switch
                {
                    ".txt" => string.Join(Environment.NewLine + Environment.NewLine, transcript.Regions.Select(region => region.FinalText)),
                    ".md" => "# Transcript\n\n" + string.Join("\n\n", transcript.Regions.Select(region => $"**{(region.NativeTimestamps ? Timestamp(region.StartMs) : "No native timestamps")}**\n\n{EscapeMarkdown(region.FinalText)}")),
                    ".json" => JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true }),
                    ".csv" => "startMs,endMs,finalText,whisperText,canaryText,source,confidence\r\n" + string.Join("\r\n", transcript.Regions.Select(region =>
                        $"{(region.NativeTimestamps ? region.StartMs.ToString(CultureInfo.InvariantCulture) : "")},{(region.NativeTimestamps ? region.EndMs.ToString(CultureInfo.InvariantCulture) : "")},{Csv(region.FinalText)},{Csv(region.WhisperText)},{Csv(region.CanaryText)},{Csv(region.Source)},{region.Confidence?.ToString(CultureInfo.InvariantCulture)}")),
                    ".srt" => string.Join("\n", transcript.Regions.Where(region => region.FinalText.Length > 0).Select((region, index) => $"{index + 1}\n{Timestamp(region.StartMs, ',')} --> {Timestamp(region.EndMs, ',')}\n{SubtitleText(region.FinalText)}\n")),
                    ".vtt" => "WEBVTT\n\n" + string.Join("\n", transcript.Regions.Where(region => region.FinalText.Length > 0).Select(region => $"{Timestamp(region.StartMs)} --> {Timestamp(region.EndMs)}\n{SubtitleText(region.FinalText)}\n")),
                    _ => throw new ArgumentException("Choose a supported export format: TXT, MD, JSON, CSV, SRT, VTT or DOCX.")
                };
                await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), token);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static string SubtitleText(string text) => text.Replace("\r", " ").Replace("\n", " ").Replace("-->", "→");
    private static string EscapeMarkdown(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("<", "&lt;");
    private static void WriteDocx(FinalTranscript transcript, string path)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static XElement Paragraph(XNamespace ns, string text, bool bold = false) => new(ns + "p", new XElement(ns + "r",
            bold ? new XElement(ns + "rPr", new XElement(ns + "b")) : null, new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
        var body = new XElement(w + "body", Paragraph(w, "Transcript", true));
        foreach (var region in transcript.Regions) { body.Add(Paragraph(w, region.NativeTimestamps ? Timestamp(region.StartMs) : "No native timestamps", true)); body.Add(Paragraph(w, region.FinalText)); }
        body.Add(new XElement(w + "sectPr", new XElement(w + "pgSz", new XAttribute(w + "w", 11906), new XAttribute(w + "h", 16838)),
            new XElement(w + "pgMar", new XAttribute(w + "top", 1134), new XAttribute(w + "right", 1134), new XAttribute(w + "bottom", 1134), new XAttribute(w + "left", 1134))));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string contents) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(contents); }
        Entry("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Entry("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Entry("word/document.xml", new XDocument(new XElement(w + "document", body)).ToString());
    }
}
