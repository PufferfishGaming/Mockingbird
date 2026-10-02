using CommunityToolkit.Mvvm.ComponentModel;
using TriAsr.Hardware;

namespace TriAsr.App;

/// <summary>The Settings choice of how much of the computer a job may use (see <see cref="ResourceGovernor"/>).</summary>
public sealed partial class ShellViewModel
{
    public IReadOnlyList<string> ResourceProfiles { get; } = [Loc.Key("Auto"), Loc.Key("Quiet"), Loc.Key("Default"), Loc.Key("Max")];
    [ObservableProperty] private string _selectedResourceProfile = "Auto";
    [ObservableProperty] private string _resourceSummary = "";

    partial void OnSelectedResourceProfileChanged(string value)
    {
        governor.Profile = Enum.TryParse<ResourceProfile>(value, out var profile) ? profile : ResourceProfile.Auto;
        RefreshResourceSummary();
        if (_initialized) Persist();
    }

    private void RestoreResourceSettings(AppSettings settings)
    {
        SelectedResourceProfile = settings.ResourceProfile;
        RefreshResourceSummary();
    }

    public void RefreshResourceSummary()
    {
        var budget = governor.Current();
        var effective = T(budget.Effective.ToString());
        var sentence = budget.Requested == ResourceProfile.Auto
            ? T("Auto is using the {0} profile because the computer is {1}: at most {2} CPU threads.", effective,
                budget.PowerSource switch { PowerSource.Battery => T("running on battery"), PowerSource.Ac => T("plugged in"), _ => T("on an unknown power source") }, budget.Threads)
            : T("The {0} profile allows at most {1} CPU threads.", effective, budget.Threads);
        ResourceSummary = sentence + " " + T("Saved thread counts above this limit are reduced automatically when a job starts.");
    }
}
