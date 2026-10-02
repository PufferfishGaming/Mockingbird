namespace TriAsr.App;

/// <summary>The decisions behind first-run setup, kept free of window state so they can be tested on their own.</summary>
public static class SetupPlan
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Skipped = "skipped";

    public static string Normalize(string? state) => state is Done or Skipped ? state : Pending;

    /// <summary>Setup is offered until the user has answered, and only while it would still do something: a speech model is missing, or speed was never tuned.</summary>
    public static bool ShouldOffer(string state, bool requiredModelsMissing, bool tuned) => state == Pending && (requiredModelsMissing || !tuned);

    /// <summary>How much of the download is on disk, 0 to 1: finished files count fully and partial files by the bytes already received.</summary>
    public static double DownloadFraction(IEnumerable<(long Total, long OnDisk)> files)
    {
        var list = files.ToArray();
        var total = list.Sum(file => file.Total);
        return total <= 0 ? 1 : Math.Clamp((double)list.Sum(file => Math.Min(file.OnDisk, file.Total)) / total, 0, 1);
    }
}
