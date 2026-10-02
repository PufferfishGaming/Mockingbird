using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Infrastructure;

/// <summary>
/// Deletes a project: its record and everything stored for it, the working files in the job's folder, and the copy of the recording
/// that the program itself made (a recording that another computer uploaded, the sound fetched from a link). A recording the person chose or
/// recorded themselves is never touched.
/// </summary>
public sealed class ProjectRemoval(IJobRepository repository, IJobWorkspace workspace, IStoragePaths paths)
{
    /// <summary>Only a project that is not being worked on can be deleted: one that is finished, has failed or was cancelled.</summary>
    public static bool CanDelete(JobState state) => state is JobState.Complete or JobState.Failed or JobState.Cancelled;

    /// <returns>The folders that could not be removed completely (a file in them was in use). The project itself is gone either way.</returns>
    public async Task<IReadOnlyList<string>> DeleteAsync(TranscriptionJob job, CancellationToken token = default)
    {
        if (!CanDelete(job.State)) throw new InvalidOperationException("A project that is still being worked on cannot be deleted.");
        await repository.DeleteAsync(job.Id, token).ConfigureAwait(false);
        var leftovers = new List<string>();
        foreach (var folder in FoldersOf(job)) await RemoveFolderAsync(folder, leftovers, token).ConfigureAwait(false);
        return leftovers;
    }

    /// <summary>The folders that belong to the project and to nobody else, each checked to lie inside the program's own data folder.</summary>
    public IEnumerable<string> FoldersOf(TranscriptionJob job)
    {
        var root = Path.GetFullPath(paths.Root);
        var working = Path.GetFullPath(workspace.DirectoryFor(job.Id));
        if (IsInside(working, Path.Combine(root, "Jobs"))) yield return working;
        // The copy the program made of the recording lies in a folder of its own: Api/Incoming/<id>/file or Links/<id>/file.
        foreach (var owned in new[] { Path.Combine(root, "Api", "Incoming"), Path.Combine(root, "Links") })
            if (OwnFolderUnder(job.SourcePath, owned) is { } folder) yield return folder;
    }

    private static string? OwnFolderUnder(string sourcePath, string ownedRoot)
    {
        string source;
        try { source = Path.GetFullPath(sourcePath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!IsInside(source, ownedRoot)) return null;
        var relative = Path.GetRelativePath(ownedRoot, source);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        // A file lying directly in the owned root has no folder of its own and is left alone.
        return relative.Length > first.Length ? Path.Combine(ownedRoot, first) : null;
    }

    private static bool IsInside(string path, string folder)
    {
        var inner = Path.GetFullPath(path);
        var outer = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A player or a waveform may still be letting go of a file, so a locked file is tried a few times before it is given up on.</summary>
    private static async Task RemoveFolderAsync(string folder, List<string> leftovers, CancellationToken token)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt < 4) await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
        leftovers.Add(folder);
    }
}
