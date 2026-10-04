using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace TriAsr.App;

/// <summary>The PowerShell terminal of the app (Studio and the Server window): new output scrolls into view while Auto-scroll is on.</summary>
public partial class TerminalPage : System.Windows.Controls.UserControl
{
    private ShellViewModel? _model;

    public TerminalPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as ShellViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(ShellViewModel.TerminalOutput) && _model?.TerminalAutoScroll == true)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { TerminalOutputBox.UpdateLayout(); TerminalOutputBox.ScrollToEnd(); }));
    }

    private void TerminalInputKeyDown(object sender, KeyEventArgs args)
    {
        if (_model is not { } vm) return;
        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        { if (vm.SubmitTerminalCommand.CanExecute(null)) vm.SubmitTerminalCommand.Execute(null); args.Handled = true; }
        else if (args.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.None)
        { vm.RecallTerminalCommand(args.Key == Key.Up ? -1 : 1); args.Handled = true; }
    }
}
