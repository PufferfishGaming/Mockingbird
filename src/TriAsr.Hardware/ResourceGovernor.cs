using System.Runtime.Versioning;

namespace TriAsr.Hardware;

/// <summary>How much of the computer a transcription job may use. No profile ever uses every core.</summary>
public enum ResourceProfile { Auto, Quiet, Default, Max }

public sealed record ResourceBudget(ResourceProfile Requested, ResourceProfile Effective, int Threads, string PowerSource)
{
    public string Summary => Requested == ResourceProfile.Auto
        ? $"Auto is using the {Effective} profile because the computer is {Describe(PowerSource)}: at most {Threads} CPU threads."
        : $"The {Effective} profile allows at most {Threads} CPU threads.";

    private static string Describe(string power) => power switch { Hardware.PowerSource.Battery => "running on battery", Hardware.PowerSource.Ac => "plugged in", _ => "on an unknown power source" };
}

/// <summary>
/// Keeps long jobs from making the computer unusable: a shared CPU thread budget derived from the number of physical
/// cores. Quiet is half the cores, Default leaves four free, Max leaves two free. Auto picks Quiet on battery and
/// Default otherwise. GPU-heavy engines use few CPU threads, so the limit mostly matters for CPU runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResourceGovernor
{
    private readonly Lazy<int> _physicalCores;
    private readonly Func<string> _powerSource;

    public ResourceGovernor() : this(() => HardwareProfiler.ReadTopology().PhysicalCores, PowerSource.Read) { }

    /// <summary>For tests and tools that supply their own core count and power source.</summary>
    public ResourceGovernor(Func<int> physicalCores, Func<string> powerSource)
    {
        _physicalCores = new(() => Math.Max(1, physicalCores()));
        _powerSource = powerSource;
    }

    public ResourceProfile Profile { get; set; } = ResourceProfile.Auto;

    /// <summary>Threads for a concrete profile. <see cref="ResourceProfile.Auto"/> is treated as Default.</summary>
    public static int ThreadsFor(ResourceProfile profile, int physicalCores)
    {
        physicalCores = Math.Max(1, physicalCores);
        var quiet = Math.Max(1, physicalCores / 2);
        var normal = Math.Max(quiet, physicalCores - 4);
        var max = Math.Max(normal, physicalCores - 2);
        return profile switch { ResourceProfile.Quiet => quiet, ResourceProfile.Max => max, _ => normal };
    }

    public ResourceBudget Current() => For(Profile);

    public ResourceBudget For(ResourceProfile requested)
    {
        var power = _powerSource();
        var effective = requested != ResourceProfile.Auto ? requested : power == PowerSource.Battery ? ResourceProfile.Quiet : ResourceProfile.Default;
        return new(requested, effective, ThreadsFor(effective, _physicalCores.Value), power);
    }

    /// <summary>Limits a requested thread count to the current budget (never below one).</summary>
    public int Clamp(int requested) => Math.Clamp(requested, 1, Current().Threads);
}
