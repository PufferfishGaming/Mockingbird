using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TriAsr.App;

public sealed partial class ShellViewModel
{
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorTitle = "Something needs attention";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _animateErrors = true;
    public bool PulseError => HasError && AnimateErrors && System.Windows.SystemParameters.ClientAreaAnimation
        && !System.Windows.SystemParameters.HighContrast;
    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(PulseError));
    partial void OnAnimateErrorsChanged(bool value)
    { OnPropertyChanged(nameof(PulseError)); if (_initialized) Persist(); }
    public void ReportError(string title, string message)
    {
        HasError = false;
        ErrorTitle = title;
        ErrorMessage = message;
        Status = title + ": " + message;
        HasError = true;
    }
    [RelayCommand] private void DismissError() => HasError = false;
}
