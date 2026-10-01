namespace TriAsr.Hardware;

/// <summary>Plain-language problems found while probing the machine. Pure, so it is tested without hardware.</summary>
public static class HardwareIssues
{
    public static IReadOnlyList<string> Evaluate(string osArchitecture, string processArchitecture, IReadOnlyList<ProbeError> probeErrors)
    {
        var issues = new List<string>();
        if (osArchitecture != "unknown" && processArchitecture != "unknown" && !string.Equals(osArchitecture, processArchitecture, StringComparison.OrdinalIgnoreCase))
            issues.Add($"Mockingbird Studio is a {processArchitecture} program running under emulation on a {osArchitecture} computer. " +
                $"It will work but slowly, and hardware detection can be wrong. Install the native {osArchitecture} version when one is available.");
        foreach (var error in probeErrors)
            issues.Add($"Could not read {error.Probe.Replace('-', ' ')} ({error.ExceptionType}). Its details are missing from this report, which does not mean the hardware is absent.");
        return issues;
    }
}
