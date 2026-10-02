using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>
/// <c>{local:T 'Some text'}</c> in XAML: the text in the interface language, refreshed when the language changes.
/// Put quotes around the text when it contains a comma.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Version)) { Source = Loc.Instance, Mode = BindingMode.OneWay, Converter = TextOfParameter.Instance, ConverterParameter = Text };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class TextOfParameter : IValueConverter
    {
        public static TextOfParameter Instance { get; } = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>
/// Translates text that comes from the view model (the names of themes, presets, job states, languages...). Used in a MultiBinding whose second
/// value is <see cref="Loc.Version"/>, so the text refreshes when the language changes: the bound value only ever holds the English text.
/// </summary>
public sealed class TranslateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => Text(values.Length > 0 ? values[0] : null);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();

    public static string Text(object? value) => value switch
    {
        null => "",
        LanguageOption language => LanguageText.Of(language),
        JobState state => JobText.State(state),
        string text => Loc.Describe(text),
        _ => Loc.T(value.ToString() ?? "")
    };
}

/// <summary>Names of languages the way the interface shows them.</summary>
public static class LanguageText
{
    public static string Of(LanguageOption language) => language.Code == "auto" ? Loc.T(language.Name) : $"{Loc.T(language.Name)} ({language.Code})";

    /// <summary>The languages in alphabetical order of the names the interface shows, by the rules of the interface language (the catalog itself keeps Whisper's order).</summary>
    public static IReadOnlyList<LanguageOption> InOrder(IEnumerable<LanguageOption> languages)
    {
        var compare = CultureInfo.GetCultureInfo(Loc.Instance.Language).CompareInfo;
        return languages.OrderBy(Of, Comparer<string>.Create((first, second) => compare.Compare(first, second, CompareOptions.IgnoreCase))).ToArray();
    }
}

/// <summary>Job states and checkpoints in plain words.</summary>
public static class JobText
{
    public static string State(JobState state) => state switch
    {
        JobState.Queued => Loc.T("Queued"),
        JobState.Complete => Loc.T("Complete"),
        JobState.Cancelled => Loc.T("Cancelled"),
        JobState.Failed => Loc.T("Failed"),
        _ => Loc.T(TranscriptionProgressTracker.StageName(state))
    };

    /// <summary>A checkpoint is the name of a state, or "normalized" once the audio has been converted.</summary>
    public static string Checkpoint(string text) => Enum.TryParse<JobState>(text, out var state) ? State(state) : text == "normalized" ? Loc.T("Audio prepared") : Loc.T(text);
}

/// <summary>How a transcript region came about, in plain words. The saved identifier stays as it is; only what is shown is a sentence.</summary>
public static class SourceText
{
    public static string Of(string source) => Loc.T(source switch
    {
        "single-asr-needs-listening" => Loc.Key("one engine · needs listening"),
        "llm-arbitrated" => Loc.Key("chosen by the correction model"),
        _ => source
    });
}

/// <summary>The source of a region (see <see cref="SourceText"/>), for use in a MultiBinding with <see cref="Loc.Version"/>.</summary>
public sealed class SourceConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length > 0 && values[0] is string source ? SourceText.Of(source) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The checkpoint of a job (a stage name or "normalized") translated, for use in a MultiBinding with <see cref="Loc.Version"/>.</summary>
public sealed class CheckpointConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length > 0 && values[0] is string text ? JobText.Checkpoint(text) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public enum TrKind { Text, Checkpoint, Source, BenchmarkMeaning, BenchmarkBackend }

/// <summary>
/// <c>{local:Tr Path}</c> in XAML: the bound value shown in the interface language, refreshed when the language changes. The value itself stays
/// English (it is also what the program compares and saves); only what is displayed is translated. <c>.</c> binds the item itself.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string Path { get; set; }

    public TrKind Kind { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var multi = new MultiBinding { Mode = BindingMode.OneWay, Converter = Kind switch
        {
            TrKind.Checkpoint => new CheckpointConverter(),
            TrKind.Source => new SourceConverter(),
            TrKind.BenchmarkMeaning => new BenchmarkTextConverter(false),
            TrKind.BenchmarkBackend => new BenchmarkTextConverter(true),
            _ => new TranslateConverter()
        } };
        multi.Bindings.Add(Path == "." ? new Binding() : new Binding(Path));
        multi.Bindings.Add(new Binding(nameof(Loc.Version)) { Source = Loc.Instance, Mode = BindingMode.OneWay });
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetProperty: System.Reflection.PropertyInfo property } && typeof(BindingBase).IsAssignableFrom(property.PropertyType))
            return multi; // a DataGrid column takes the binding itself
        return multi.ProvideValue(serviceProvider);
    }
}
