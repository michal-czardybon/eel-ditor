using AvaloniaEdit.Document;
using EelDitor.App.Services;
using EelDitor.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EelDitor.App.Documents;

public enum DiskState { Normal, ChangedOnDisk, Deleted }

/// <summary>One open file (or untitled buffer) with everything needed to save it back byte-exactly.</summary>
public sealed partial class EditorDocument : ObservableObject
{
    private static int _untitledCounter;

    public EditorDocument(TextDocument document, string? filePath)
    {
        Document = document;
        _filePath = filePath is null ? null : Path.GetFullPath(filePath);
        if (filePath is null) UntitledNumber = ++_untitledCounter;
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UndoStack.IsOriginalFile)) return;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Title));
        };
    }

    public TextDocument Document { get; }

    public int UntitledNumber { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(DirectoryPath), nameof(IsUntitled), nameof(Title))]
    private string? _filePath;

    public bool IsUntitled => FilePath is null;

    public string FileName => FilePath is null ? Loc.T("Doc.Untitled", UntitledNumber) : Path.GetFileName(FilePath);

    public string DirectoryPath => FilePath is null ? "" : Path.GetDirectoryName(FilePath) ?? "";

    public string Title => (IsDirty ? "● " : "") + FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(Title))]
    private bool _metadataDirty;

    /// <summary>Unsaved text changes, or an encoding/EOL conversion not yet written.</summary>
    public bool IsDirty => !Document.UndoStack.IsOriginalFile || MetadataDirty;

    [ObservableProperty]
    private TextEncoding _encoding = TextEncoding.Utf8;

    /// <summary>Line terminator used for new files and for conversions; existing lines keep their own.</summary>
    [ObservableProperty]
    private EolKind _eol = EolConverter.PlatformDefault;

    [ObservableProperty]
    private bool _eolMixed;

    [ObservableProperty]
    private bool _isLossyDecode;

    [ObservableProperty]
    private bool _looksBinary;

    [ObservableProperty]
    private DiskState _diskState;

    [ObservableProperty]
    private string _syntaxName = "";

    [ObservableProperty]
    private bool _pinned;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Git status letter (M, A, ?, D, U, R) or empty.</summary>
    /// <summary>Internal git letter: M modified, A added, ? untracked, D deleted, R renamed, U conflict; "" = clean or not in a repo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitBadge), nameof(GitModified), nameof(GitAdded), nameof(GitDeleted), nameof(GitConflict), nameof(Tooltip))]
    private string _gitStatus = "";

    /// <summary>Root of the git repository the file belongs to, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _gitRoot;

    /// <summary>Letter shown in the files panel (VS Code style: U = untracked/new, C = conflict).</summary>
    public string GitBadge => GitStatus switch { "?" => "U", "U" => "C", var s => s };

    public bool GitModified => GitStatus is "M" or "R";
    public bool GitAdded => GitStatus is "A" or "?";
    public bool GitDeleted => GitStatus is "D";
    public bool GitConflict => GitStatus is "U";

    /// <summary>Full path, plus the git state for files in a repository.</summary>
    public string? Tooltip => FilePath is null ? null
        : GitRoot is null ? FilePath
        : FilePath + Environment.NewLine + Services.Loc.T("Git.InRepo", Services.Loc.T(Core.Git.GitService.StatusKey(GitStatus)));

    public bool IsLarge { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }

    public long FileSize { get; set; }

    /// <summary>Hash of the bytes we last read or wrote, to ignore our own writes in the file watcher.</summary>
    public string? LastKnownHash { get; set; }

    /// <summary>"Display as": a TextMate language id, <see cref="PlainTextSyntax"/> or <see cref="MarkdownSyntax"/>; null = by extension.</summary>
    public string? SyntaxOverride { get; set; }

    public const string PlainTextSyntax = "plaintext";
    public const string MarkdownSyntax = "markdown";

    // A Git revision opened as a copy still uses the original filename to pick its syntax.
    internal string? RevisionPath { get; init; }
    internal string? SyntaxPath => FilePath ?? RevisionPath;

    public bool IsMarkdown => SyntaxOverride is { } o
        ? o == MarkdownSyntax
        : SyntaxPath is not { } path || Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";

    // View state restored when the document is shown again.
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }

    /// <summary>Most recently used ordering (higher = more recent).</summary>
    public long LastActivated { get; set; }

    /// <summary>Shown in Cyrillic (display only, the file is unchanged); null = as written.</summary>
    public string? CyrillicView { get; set; }

    /// <summary>Long lines were just converted: the info bar offers to show the changes as a word diff.</summary>
    public string? ReflowNotice { get; set; }

    /// <summary>The text the notice is about: any later change (typing, Ctrl+Z) makes it stale, and it goes.</summary>
    public AvaloniaEdit.Document.ITextSourceVersion? ReflowVersion { get; set; }

    /// <summary>The user chose to leave the mixed line endings as they are (the bar is not shown again for them).</summary>
    public bool EolMixedAccepted { get; set; }

    /// <summary>
    /// After edits that cancel out (typing and deleting, undoing by hand) the text can be identical to the file again
    /// while the undo stack says it is changed. If the encoded bytes equal what is on disk, the document is clean.
    /// </summary>
    public void ClearDirtyIfSameAsDisk()
    {
        if (Document.UndoStack.IsOriginalFile || MetadataDirty || FilePath is null || LastKnownHash is null
            || IsLarge || Document.TextLength > 4 * 1024 * 1024) return;
        try
        {
            var bytes = Core.Text.TextFile.Encode(Document.Text, Encoding);
            if (Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)) == LastKnownHash)
                Document.UndoStack.MarkAsOriginalFile();
        }
        catch (Core.Text.UnencodableCharactersException)
        {
            // Not encodable in the file's encoding: certainly different from the file.
        }
    }

    public void MarkSaved()
    {
        Document.UndoStack.MarkAsOriginalFile();
        MetadataDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    partial void OnDiskStateChanged(DiskState value) => OnPropertyChanged(nameof(Title));

    public void RaiseTitleChanged() => OnPropertyChanged(nameof(Title));
}
