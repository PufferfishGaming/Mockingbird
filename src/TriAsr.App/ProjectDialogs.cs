using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TriAsr.App;

/// <summary>What the project lists of the windows share: the question before a project is deleted, and telling a click on a row from a click on a button in it.</summary>
internal static class ProjectDialogs
{
    /// <summary>Asks whether to delete a project. <paramref name="onServer"/> is true when the recording is on another computer, which then removes the copy it holds.</summary>
    public static bool ConfirmDelete(Window? owner, string name, bool onServer)
    {
        var text = onServer
            ? Loc.T("Delete \"{0}\" from the server? Its transcript, the edits and the recording the server holds are removed. This cannot be undone.", name)
            : Loc.T("Delete \"{0}\"? Its transcript, your edits and the working files are removed from this computer, and so is a copy of the recording that was uploaded or fetched from a link. A recording file you chose yourself is not deleted. This cannot be undone.", name);
        var title = Loc.T("Delete project");
        var answer = owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>Asks whether to delete a note. Where the note is kept (this computer, or the server) is not the question: it is gone from there for good.</summary>
    public static bool ConfirmDeleteNote(Window? owner, string name)
    {
        var text = Loc.T("Delete \"{0}\"? The note and its text are removed. This cannot be undone.", name);
        var title = Loc.T("Delete note");
        var answer = owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>Whether the element is, or is inside, a button of the row (below <paramref name="row"/>), so that a click on "Delete" is not also a click on the row.</summary>
    public static bool IsInsideButton(DependencyObject? element, DependencyObject row)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, row); current = ParentOf(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
