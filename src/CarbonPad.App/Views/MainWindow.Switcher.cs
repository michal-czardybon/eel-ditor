namespace CarbonPad.App.Views;

public partial class MainWindow
{
    private DocumentSwitcher? _switcher;

    private DocumentSwitcher Switcher
    {
        get
        {
            if (_switcher is not null) return _switcher;
            _switcher = new DocumentSwitcher(EditorHost, d => { Activate(d); Editor.Focus(); });
            EditorHost.Children.Add(_switcher.Popup);
            return _switcher;
        }
    }

    private void ShowSwitcher(bool backwards)
    {
        var mru = _files.MostRecentlyUsed();
        if (mru.Count < 2) return;
        Switcher.ShowMru(mru, backwards);
    }

    private void ShowQuickOpen() => Switcher.ShowQuickOpen(_files.Documents);
}
