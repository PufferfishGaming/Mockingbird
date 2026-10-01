using CommunityToolkit.Mvvm.ComponentModel;

namespace TriAsr.App;

public sealed class BootstrapViewModel : ObservableObject
{
    public string Title => "TriASR · Repository bootstrap";
    public string Message => "Milestone 0\n\n.NET 10 · WPF · x64\nDependency injection and local logging are ready.\n\nThe application shell is planned for milestone 1.";
}