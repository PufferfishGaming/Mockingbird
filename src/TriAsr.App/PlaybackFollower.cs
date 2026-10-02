namespace TriAsr.App;

/// <summary>Works out which region the recording is in and how far. Studio's review and the Client's both use it, from their player timers.</summary>
public static class PlaybackFollower
{
    /// <summary>
    /// Sets <see cref="ReviewRegion.PlayProgress"/> of every region for the position (milliseconds) of the recording, and returns the region it is in.
    /// A position between two regions, or before the first, belongs to none. Regions without their own timestamps are skipped.
    /// </summary>
    public static ReviewRegion? Follow(IEnumerable<ReviewRegion> regions, long positionMs)
    {
        ReviewRegion? active = null;
        foreach (var region in regions)
        {
            var original = region.Original;
            var inside = original.NativeTimestamps && positionMs >= original.StartMs && positionMs < original.EndMs && active is null;
            var progress = inside ? Math.Clamp((positionMs - original.StartMs) / (double)Math.Max(1, original.EndMs - original.StartMs), 0, 1) : -1;
            if (inside) active = region;
            if (region.PlayProgress != progress) region.PlayProgress = progress;
        }
        return active;
    }

    /// <summary>Clears the marks (the recording was closed or the transcript changed).</summary>
    public static void Clear(IEnumerable<ReviewRegion> regions)
    {
        foreach (var region in regions) if (region.PlayProgress != -1) region.PlayProgress = -1;
    }
}
