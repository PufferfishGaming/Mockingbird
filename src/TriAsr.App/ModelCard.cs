using CommunityToolkit.Mvvm.ComponentModel;
using TriAsr.Infrastructure;

namespace TriAsr.App;

public sealed partial class ModelCard(ModelEntry entry, string location) : ObservableObject
{
    public ModelEntry Entry { get; } = entry;
    public string Title => $"{Entry.Family} · {Entry.Name} · {Entry.Quantization}";
    public string Details => $"{Entry.Bytes / 1073741824d:0.00} GiB · {Entry.License}";
    public string Location { get; } = location;
    [ObservableProperty] private string _status = System.IO.File.Exists(location) ? "Installed · checksum not yet verified" : "Not installed";
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private bool _recommended;
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private double _downloadPercent;
    public bool Highlighted => Selected || Recommended;
    partial void OnSelectedChanged(bool value) => OnPropertyChanged(nameof(Highlighted));
    partial void OnRecommendedChanged(bool value) => OnPropertyChanged(nameof(Highlighted));
    public void Refresh(ModelInstallation installation)
    {
        Installed = installation.Installed && !installation.WrongSize;
        DownloadPercent = Installed ? 100 : Math.Clamp(installation.PartialBytes / (double)Entry.Bytes * 100, 0, 100);
        Status = installation.WrongSize ? "File size mismatch · verify or move the file aside"
            : installation.Verified ? "Downloaded · verified · ready offline"
            : Installed ? "Downloaded · verify once to confirm integrity"
            : installation.PartialBytes > 0 ? $"Paused · {DownloadPercent:0}% saved · resume available" : "Not downloaded";
    }
}
