using System.IO;

namespace TriAsr.App;

/// <summary>Transcribing a link on Studio's New transcription page (ADR-0018): the sound is fetched on this computer and becomes the file to transcribe.</summary>
public sealed partial class ShellViewModel
{
    private LinkHelperViewModel? _linkHelper;
    private LinkViewModel? _link;

    /// <summary>The helper that fetches the sound of web pages: installed or not, and the button that installs or updates it.</summary>
    public LinkHelperViewModel LinkHelper => _linkHelper ??= new LinkHelperViewModel(linkTool, OnUi);

    public LinkViewModel Link => _link ??= MakeLink();

    private LinkViewModel MakeLink()
    {
        var card = new LinkViewModel(links, LinkHelper, Path.Combine(storage.Root, "Links"), OnUi);
        card.Fetched += file => SourcePath = file.Path;
        return card;
    }
}
