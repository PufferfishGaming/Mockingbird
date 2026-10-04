using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>
/// The summary of the open transcript (Studio's Review page, a server's Review tab): made on request by a language model (on this computer, or on
/// the server), kept with the project, shown as a draft to check, and copied or saved as Markdown or text.
/// </summary>
public sealed partial class SummaryViewModel : ObservableObject
{
    /// <summary>Makes a summary of the open transcript (and keeps it with the project); reports progress from 0 to 1.</summary>
    public delegate Task<MeetingSummary> Maker(IProgress<double> progress, CancellationToken token);

    private readonly Action<Action> _onUi;
    private readonly bool _onServer;
    private Maker? _make;
    private MeetingSummary? _summary;
    private string _project = "";
    private Func<string>? _unavailable;
    private CancellationTokenSource? _work;
    private int _opened;

    /// <param name="onServer">Whether the server writes the summaries (the Client, a server's pages) rather than this computer (Studio).</param>
    public SummaryViewModel(Action<Action> onUi, bool onServer = false) { _onUi = onUi; _onServer = onServer; }

    /// <summary>What the panel does, and that its result is a draft.</summary>
    public string Intro => _onServer
        ? Loc.T("A language model on the server writes what the conversation was about, what was decided and who does what. It is a draft: check it against the transcript.")
        : Loc.T("A language model on this computer writes what the conversation was about, what was decided and who does what. It is a draft: check it against the transcript.");

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSummarize)), NotifyCanExecuteChangedFor(nameof(SummarizeCommand))] private bool _isAvailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSummarize)), NotifyCanExecuteChangedFor(nameof(SummarizeCommand))] private bool _isWorking;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isOpen;

    public bool HasProject => _make is not null;
    public bool CanSummarize => IsAvailable && !IsWorking && _make is not null;
    public bool HasSummary => _summary is not null;
    public bool NoSummary => _summary is null;
    public string Text => _summary?.Summary ?? "";
    public IReadOnlyList<string> KeyPoints => _summary?.KeyPoints ?? [];
    public IReadOnlyList<string> Decisions => _summary?.Decisions ?? [];
    public IReadOnlyList<string> ActionItems => _summary?.ActionItems.Select(Summaries.ActionLine).ToArray() ?? [];
    public IReadOnlyList<string> OpenQuestions => _summary?.OpenQuestions ?? [];
    public bool HasKeyPoints => KeyPoints.Count > 0;
    public bool HasDecisions => Decisions.Count > 0;
    public bool HasActionItems => ActionItems.Count > 0;
    public bool HasOpenQuestions => OpenQuestions.Count > 0;
    public string SummarizeLabel => HasSummary ? Loc.T("Summarize again") : Loc.T("Summarize");
    public string Unavailable => IsAvailable ? "" : _unavailable?.Invoke() ?? "";
    /// <summary>Who wrote it and when, with the reminder that it is a draft.</summary>
    public string MadeWith => _summary is null ? "" : Loc.T("Written by the language model {0} on {1}. It is a draft: check it against the transcript before you rely on it.",
        Path.GetFileNameWithoutExtension(_summary.Model), _summary.CreatedUtc.ToLocalTime().ToString("g"));

    /// <summary>Shows the summary of a newly opened transcript (or none yet), and how to make one.</summary>
    /// <param name="unavailable">Why no summary can be made here, in the interface's language (asked again when the language changes).</param>
    public void Open(string project, MeetingSummary? summary, bool available, Func<string> unavailable, Maker make)
    {
        _work?.Cancel();
        _opened++;
        _project = project; _summary = summary; _make = make; _unavailable = unavailable;
        IsAvailable = available; IsWorking = false; Percent = 0; Status = "";
        Changed();
    }

    /// <summary>No transcript is open.</summary>
    public void Clear()
    {
        _work?.Cancel();
        _opened++;
        _project = ""; _summary = null; _make = null; _unavailable = null;
        IsAvailable = false; IsWorking = false; Percent = 0; Status = "";
        Changed();
    }

    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private async Task SummarizeAsync()
    {
        if (_make is not { } make) return;
        var opened = _opened;
        var work = _work = new CancellationTokenSource();
        IsWorking = true; Percent = 0; IsOpen = true;
        Status = Loc.T("Summarizing… this takes from a few seconds to a few minutes, depending on the length and the computer.");
        try
        {
            var made = await make(new Progress<double>(value => _onUi(() => { if (opened == _opened) Percent = Math.Round(value * 100); })), work.Token);
            if (opened != _opened) return;
            _summary = made; Status = "";
            Changed();
        }
        catch (OperationCanceledException) { if (opened == _opened) Status = Loc.T("The summary was cancelled."); }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException or System.Net.Http.HttpRequestException or TriAsr.Infrastructure.RemoteException)
        {
            if (opened == _opened) Status = Loc.T("The summary could not be made: {0}", Loc.Describe(error.Message));
        }
        finally { if (opened == _opened) IsWorking = false; }
    }

    [RelayCommand] private void Cancel() => _work?.Cancel();

    /// <summary>The summary as Markdown, its headings in the interface's language.</summary>
    public string Markdown() => _summary is null ? "" : Summaries.ToMarkdown(_summary, _project, new Summaries.Headings(Loc.T("Summary"), Loc.T("Summary"),
        Loc.T("Key points"), Loc.T("Decisions"), Loc.T("Action items"), Loc.T("Open questions"),
        _onServer ? Loc.T("Written by a language model on the server from the transcript; check it before you rely on it.")
            : Loc.T("Written by a language model on this computer from the transcript; check it before you rely on it.")));

    /// <summary>Copies the summary as text; null when it worked, otherwise why not.</summary>
    [RelayCommand]
    private void Copy()
    {
        if (_summary is null) return;
        try { System.Windows.Clipboard.SetText(Markdown()); Status = Loc.T("The summary was copied."); }
        catch (System.Runtime.InteropServices.ExternalException error) { Status = Loc.T("The summary could not be copied: {0}", Loc.Describe(error.Message)); }
    }

    /// <summary>Saves the summary: Markdown for .md, plain text (the same, it reads well as text) for anything else.</summary>
    public async Task SaveAsync(string path)
    {
        if (_summary is null) return;
        try { await File.WriteAllTextAsync(path, Markdown(), new System.Text.UTF8Encoding(false)); Status = Loc.T("Summary saved: {0}", Path.GetFileName(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status = Loc.T("The summary could not be saved: {0}", Loc.Describe(error.Message)); }
    }

    /// <summary>The file name a saved summary is offered under.</summary>
    public string SuggestedFileName => Path.GetFileNameWithoutExtension(_project) + " - " + Loc.T("summary") + ".md";

    public void RefreshTexts()
    {
        foreach (var name in new[] { nameof(SummarizeLabel), nameof(Unavailable), nameof(MadeWith), nameof(Intro) }) OnPropertyChanged(name);
    }

    private void Changed()
    {
        foreach (var name in new[] { nameof(HasProject), nameof(HasSummary), nameof(NoSummary), nameof(Text), nameof(KeyPoints), nameof(Decisions), nameof(ActionItems), nameof(OpenQuestions),
            nameof(HasKeyPoints), nameof(HasDecisions), nameof(HasActionItems), nameof(HasOpenQuestions), nameof(SummarizeLabel), nameof(Unavailable), nameof(MadeWith), nameof(CanSummarize) })
            OnPropertyChanged(name);
        SummarizeCommand.NotifyCanExecuteChanged();
    }
}
