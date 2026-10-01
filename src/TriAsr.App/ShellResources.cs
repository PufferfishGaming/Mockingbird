using CommunityToolkit.Mvvm.ComponentModel;
using TriAsr.Hardware;

namespace TriAsr.App;

/// <summary>The Settings choice of how much of the computer a job may use (see <see cref="ResourceGovernor"/>).</summary>
public sealed partial class ShellViewModel
{
    public IReadOnlyList<string> ResourceProfiles { get; } = ["Auto", "Quiet", "Default", "Max"];
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
        ResourceSummary = budget.Summary + " Saved thread counts above this limit are reduced automatically when a job starts.";
    }
}
