using System.Windows;
using System.Windows.Controls;

namespace TriAsr.App;

/// <summary>The notes page: the list, the open note and its recording. Studio shows this computer's notes; the Client and the Remote server page show the server's.</summary>
public partial class NotesView : UserControl
{
    private NotesViewModel? _model;

    public NotesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as NotesViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
        };
    }

    /// <summary>While a note is recorded, the end of it stays in view: that is where the new words appear.</summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(NotesViewModel.Text) || _model is not { IsListening: true }) return;
        NoteEditor.CaretIndex = NoteEditor.Text.Length;
        NoteEditor.ScrollToEnd();
    }

    private void CopyClick(object sender, RoutedEventArgs args)
    {
        if (_model is not { Text.Length: > 0 }) return;
        try { Clipboard.SetText(_model.Text); }
        catch (System.Runtime.InteropServices.COMException) { /* the clipboard is held by another program; the person tries again */ }
    }

    private async void DeleteClick(object sender, RoutedEventArgs args)
    {
        if (_model is not { SelectedNote: { } row } model) return;
        if (!ProjectDialogs.ConfirmDeleteNote(Window.GetWindow(this), row.DisplayTitle)) return;
        await model.DeleteOpenAsync();
    }
}
