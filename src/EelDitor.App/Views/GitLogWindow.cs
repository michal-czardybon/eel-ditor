using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using EelDitor.App.Services;
using EelDitor.Core.Git;

namespace EelDitor.App.Views;

/// <summary>Repository commits and the files changed by the selected commit.</summary>
internal sealed class GitLogWindow : Window
{
    private const string CommitColumns = "92,145,170,*";
    private const string FileColumns = "120,*";
    private readonly ListBox _commits = TableList();
    private readonly ListBox _files = TableList();
    private readonly TextBlock _commitStatus = Status();
    private readonly TextBlock _fileStatus = Status();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _fileRequest;
    private bool _closed;

    public GitLogWindow(string root)
    {
        Root = root;
        Title = Loc.T("Git.LogTitle", root);
        Width = 950;
        Height = 600;
        MinWidth = 560;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Res(BackgroundProperty, "ChromeBrush");

        _commits.ItemTemplate = new FuncDataTemplate<GitCommit?>((commit, _) => commit is null ? null
            : Row(CommitColumns, false, commit.ShortHash, commit.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), commit.Author, commit.Subject));
        _files.ItemTemplate = new FuncDataTemplate<GitCommitFile?>((file, _) => file is null ? null
            : Row(FileColumns, false, FileStatus(file.Status),
                file.OriginalPath is { } original ? $"{original} → {file.Path}" : file.Path));

        var top = Section(_commits, _commitStatus, Row(CommitColumns, true,
            Loc.T("Git.LogHash"), Loc.T("Panel.ByDate"), Loc.T("Git.LogAuthor"), Loc.T("Git.LogSubject")));
        var bottom = Section(_files, _fileStatus, Row(FileColumns, true,
            Loc.T("Git.LogChange"), Loc.T("Git.LogFile")));
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }
            .Res(BackgroundProperty, "BorderSubtleBrush");
        Grid.SetRow(splitter, 1);
        Grid.SetRow(bottom, 2);
        Content = new Grid { RowDefinitions = new RowDefinitions("3*,5,2*"), Margin = new Thickness(8), Children = { top, splitter, bottom } };

        _commits.SelectionChanged += async (_, _) => await LoadFilesAsync(root);
        Opened += async (_, _) => await LoadCommitsAsync(root);
        Closed += (_, _) =>
        {
            _closed = true;
            _fileRequest?.Cancel();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public string Root { get; }

    private async Task LoadCommitsAsync(string root)
    {
        SetStatus(_commitStatus, Loc.T("Git.LogLoading"));
        try
        {
            var commits = await GitService.LogAsync(root, max: 100, ct: _lifetime.Token);
            if (_closed) return;
            _commits.ItemsSource = commits;
            SetStatus(_commitStatus, commits.Count == 0 ? Loc.T("Git.LogEmpty") : null);
            if (commits.Count > 0) _commits.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            if (!_closed) SetStatus(_commitStatus, ex.Message);
        }
    }

    private async Task LoadFilesAsync(string root)
    {
        if (_closed) return;
        _fileRequest?.Cancel();
        _files.ItemsSource = null;
        if (_commits.SelectedItem is not GitCommit commit)
        {
            SetStatus(_fileStatus, null);
            return;
        }

        using var request = new CancellationTokenSource();
        _fileRequest = request;
        SetStatus(_fileStatus, Loc.T("Git.LogLoading"));
        try
        {
            var files = await GitService.CommitFilesAsync(root, commit.Hash, request.Token);
            if (request.IsCancellationRequested) return;
            _files.ItemsSource = files;
            SetStatus(_fileStatus, files is null ? Loc.T("Git.LogFilesFailed")
                : files.Count == 0 ? Loc.T("Git.LogNoFiles") : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            if (!request.IsCancellationRequested) SetStatus(_fileStatus, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_fileRequest, request)) _fileRequest = null;
        }
    }

    private static string FileStatus(string status) => status[0] switch
    {
        'A' or 'D' or 'M' or 'R' => Loc.T(GitService.StatusKey(status[..1])),
        _ => status,
    };

    private static ListBox TableList()
    {
        var list = new ListBox { FontSize = 12 };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(PaddingProperty, new Thickness(8, 3)) },
        });
        return list;
    }

    private static DockPanel Section(ListBox list, TextBlock status, Grid header)
    {
        var bar = new Border { Child = header, Padding = new Thickness(8, 5) }.Res(BackgroundProperty, "PanelBrush");
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(status, Dock.Top);
        return new DockPanel { Children = { bar, status, list } };
    }

    private static Grid Row(string columns, bool header, params string[] values)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        for (int i = 0; i < values.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = values[i],
                FontSize = 12,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0),
            };
            if (!header) ToolTip.SetTip(cell, values[i]);
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }
        return row;
    }

    private static TextBlock Status() => new TextBlock { IsVisible = false, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap }
        .Res(TextBlock.ForegroundProperty, "DimTextBrush");

    private static void SetStatus(TextBlock control, string? message)
    {
        control.Text = message;
        control.IsVisible = message is not null;
    }
}
