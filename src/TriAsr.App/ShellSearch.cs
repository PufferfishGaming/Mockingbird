using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>A passage of a search result, as the Projects page shows it: when, who, and the text with the match picked out.</summary>
public sealed class SearchPassageRow(Guid jobId, SearchPassage passage)
{
    public Guid JobId { get; } = jobId;
    public int Index { get; } = passage.Index;
    public string Time { get; } = passage.NativeTimestamps ? TriAsr.Export.TranscriptExporter.Timestamp(passage.StartMs)[..8] : "";
    public string Speaker => passage.Speaker is not { } speaker ? "" : passage.SpeakerNamed ? speaker : Loc.T("Speaker {0}", speaker);
    public bool HasSpeaker => passage.Speaker is not null;
    public string Before { get; } = passage.Before;
    public string Match { get; } = passage.Match;
    public string After { get; } = passage.After;
}

/// <summary>A project in the search results: its name, when it was made, how many passages match, and the first of them.</summary>
public sealed partial class SearchHitRow(SearchHit hit) : ObservableObject
{
    public Guid JobId { get; } = hit.JobId;
    public string Name { get; } = hit.Name;
    public string Created => hit.CreatedUtc.ToLocalTime().ToString("g");
    public string MatchesText => hit.Matches == 0 ? Loc.T("The name matches") : Loc.T("Matching passages: {0}", hit.Matches);
    public IReadOnlyList<SearchPassageRow> Passages { get; } = hit.Passages.Select(passage => new SearchPassageRow(hit.JobId, passage)).ToArray();
    public void RefreshTexts() => OnPropertyChanged(nameof(MatchesText));
}

/// <summary>Searching every transcript from the Projects page; a click on a passage opens the transcript there.</summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty] private string _projectSearchText = "";
    [ObservableProperty] private string _projectSearchStatus = "";
    public ObservableCollection<SearchHitRow> SearchResults { get; } = [];
    public bool HasProjectSearch => ProjectSearch.Normalize(ProjectSearchText).Length > 0;
    private CancellationTokenSource? _search;
    private readonly Dictionary<Guid, (DateTime Stamp, FinalTranscript Transcript)> _searchCache = [];

    partial void OnProjectSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasProjectSearch));
        _ = SearchProjectsAsync();
    }

    /// <summary>Runs the search again (the text changed, or a project was added, edited or deleted while results are shown).</summary>
    private async Task SearchProjectsAsync()
    {
        _search?.Cancel();
        var cancellation = _search = new CancellationTokenSource();
        var query = ProjectSearch.Normalize(ProjectSearchText);
        if (query.Length == 0) { SearchResults.Clear(); ProjectSearchStatus = ""; return; }
        ProjectSearchStatus = T("Searching…");
        var projects = Jobs.Select(job => new SearchableProject(job.Id, Path.GetFileName(job.SourcePath), job.CreatedUtc, job.State == JobState.Complete)).ToArray();
        try
        {
            var hits = await Task.Run(() => ProjectSearch.SearchAsync(projects, ReadForSearchAsync, query, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            SearchResults.Clear();
            foreach (var hit in hits) SearchResults.Add(new SearchHitRow(hit));
            ProjectSearchStatus = hits.Count == 0 ? T("Nothing found for “{0}”.", query) : T("Projects found: {0}", hits.Count);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A transcript as it was last saved (the edited one when there is one), read again only when its file changed.</summary>
    private async Task<FinalTranscript?> ReadForSearchAsync(Guid id, CancellationToken token)
    {
        var folder = workspace.DirectoryFor(id);
        var stamp = new[] { "edited.json", "final.json" }.Select(name => Path.Combine(folder, name)).Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        if (stamp == default) return null;
        lock (_searchCache) if (_searchCache.TryGetValue(id, out var cached) && cached.Stamp == stamp) return cached.Transcript;
        var transcript = await stages.LoadReviewAsync(id, token);
        lock (_searchCache) _searchCache[id] = (stamp, transcript);
        return transcript;
    }

    /// <summary>Opens the transcript of a passage and selects its region.</summary>
    [RelayCommand]
    private async Task OpenSearchPassageAsync(SearchPassageRow? passage)
    {
        if (passage is null) return;
        await OpenSearchProjectAsync(passage.JobId, passage.Index);
    }

    /// <summary>Opens a project of the results at its start (its name matched, or the person clicked the project itself).</summary>
    [RelayCommand]
    private async Task OpenSearchHitAsync(SearchHitRow? hit)
    {
        if (hit is null) return;
        await OpenSearchProjectAsync(hit.JobId, hit.Passages.FirstOrDefault()?.Index ?? 0);
    }

    private async Task OpenSearchProjectAsync(Guid jobId, int index)
    {
        if (Jobs.FirstOrDefault(job => job.Id == jobId) is not { } job) return;
        if (job.State != JobState.Complete) { SelectedJob = job; return; }
        SelectedJob = job;
        await OpenReviewAsync();
        if (index >= 0 && index < Regions.Count) SelectedRegion = Regions[index];
    }

    private void RefreshSearchTexts()
    {
        foreach (var hit in SearchResults) hit.RefreshTexts();
        if (HasProjectSearch && SearchResults.Count > 0) ProjectSearchStatus = T("Projects found: {0}", SearchResults.Count);
    }
}
