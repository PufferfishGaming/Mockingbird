using System.IO;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>The history on Studio's Projects page: a click opens a finished project in Review, and a project can be deleted.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>A click on a project: a finished one opens its transcript in Review, any other is only selected (so that Resume or Cancel apply to it).</summary>
    [RelayCommand]
    private async Task OpenProjectAsync(TranscriptionJob? job)
    {
        if (job is null) return;
        SelectedJob = job;
        if (job.State == JobState.Complete) await OpenReviewAsync();
    }

    /// <summary>
    /// Deletes a project: its transcript, edits and working files, and the copy of the recording the program made itself (an upload, a fetched link). The recording the
    /// person chose is not touched. The window asks first. One that is still being worked on has to be cancelled before.
    /// </summary>
    [RelayCommand]
    private async Task DeleteProjectAsync(TranscriptionJob? job)
    {
        job ??= SelectedJob;
        if (job is null) return;
        if (!ProjectRemoval.CanDelete(job.State)) { Status = T("A project that is still being worked on cannot be deleted. Cancel it first."); return; }
        var name = Path.GetFileName(job.SourcePath);
        try
        {
            if (IsReviewOf(job.Id)) CloseReview();          // lets go of the audio before its file is deleted
            var leftovers = await removal.DeleteAsync(job);
            Host.Forget(job.Id);
            ForgetProject(job.Id);
            Status = leftovers.Count == 0 ? T("Project deleted: {0}", name) : T("The project was deleted, but some files could not be removed: {0}", string.Join(", ", leftovers));
        }
        catch (Exception error) { ReportError(T("Could not delete the project"), error.Message); }
    }

    /// <summary>The project is gone (deleted here, or by a client through the API): the list, the selection and the open review let go of it.</summary>
    private void ForgetProject(Guid id)
    {
        if (Jobs.FirstOrDefault(item => item.Id == id) is { } existing) Jobs.Remove(existing);
        if (SelectedJob?.Id == id) SelectedJob = null;
        if (IsReviewOf(id)) CloseReview();
        if (SearchResults.FirstOrDefault(hit => hit.JobId == id) is { } found) SearchResults.Remove(found);
    }

    private bool IsReviewOf(Guid id) => _review?.JobId == id;

    private void CloseReview()
    {
        Regions.Clear();
        SelectedRegion = null;
        _review = null; _rawCanaryNote = null;
        AudioSource = null; NormalizedAudioPath = "";
        RawWhisper = ""; RawCanary = "";
        ReviewSummary = T("Open a completed project to review its transcript.");
        Summary.Clear();
    }
}
