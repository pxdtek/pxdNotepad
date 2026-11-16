using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using pxdNotepad.Data;
using pxdNotepad.Images;

namespace pxdNotepad.Views;

/// <summary>Search in the title bar: results drop down under the box as you type (every keystroke, off the UI thread).</summary>
public partial class MainView
{
    /// <summary>The search box, for MainWindow to put in its title bar.</summary>
    public Grid SearchBar { get; private set; } = null!;
    public TextBox SearchBox { get; private set; } = null!;
    public ListBox Results { get; private set; } = null!;
    private Popup _popup = null!;
    private TextBlock _resultsInfo = null!, _hint = null!;
    private Button _clear = null!;
    private readonly ObservableCollection<NoteItem> _results = [];
    private int _searchSeq;

    public string SearchText => SearchBox.Text.Trim();

    private void BuildSearch()
    {
        SearchBox = new TextBox { Padding = new Thickness(28, 0, 26, 0), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        SearchBox.SetResourceReference(Control.BackgroundProperty, "Panel2");
        var icon = MarkupWindow.Glyph("M6.5,1.5 A5,5 0 1 1 6.49,1.5 Z M10.2,10.2 L14.5,14.5");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(8, 0, 0, 0);
        icon.IsHitTestVisible = false;
        icon.Opacity = 0.7;
        _hint = new TextBlock { Text = "Search notes   Ctrl+F", Margin = new Thickness(31, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        _clear = new Button { Content = MarkupWindow.Glyph("M4,4 L12,12 M12,4 L4,12"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 22, Height = 22, Margin = new Thickness(0, 0, 2, 0), Visibility = Visibility.Collapsed, ToolTip = "Clear (Esc)" };
        _clear.SetResourceReference(StyleProperty, "ToolButton");
        _clear.Click += (_, _) => { SearchBox.Text = ""; SearchBox.Focus(); };

        Results = new ListBox { MaxHeight = 480, ItemTemplate = (DataTemplate)FindResource("ResultTemplate"), ItemsSource = _results, Focusable = false };
        ScrollViewer.SetHorizontalScrollBarVisibility(Results, ScrollBarVisibility.Disabled);
        Results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemUnder<ListBoxItem>(e.OriginalSource) is { DataContext: NoteItem it }) PickResult(it);
        };
        _resultsInfo = new TextBlock { Margin = new Thickness(14, 8, 14, 4), FontSize = 11.5 };
        _resultsInfo.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var panel = new DockPanel();
        DockPanel.SetDock(_resultsInfo, Dock.Top);
        panel.Children.Add(_resultsInfo);
        panel.Children.Add(Results);
        var card = new Border { Child = panel, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(0, 0, 0, 6), Margin = new Thickness(0, 4, 0, 0), Width = 560 };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        _popup = new Popup { Child = card, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None, Focusable = false };

        SearchBar = new Grid { Width = 420, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        SearchBar.Children.Add(SearchBox);
        SearchBar.Children.Add(icon);
        SearchBar.Children.Add(_hint);
        SearchBar.Children.Add(_clear);
        SearchBar.Children.Add(_popup);
        _popup.PlacementTarget = SearchBar;
        _popup.HorizontalOffset = (420 - 560) / 2.0;
        WindowChrome.SetIsHitTestVisibleInChrome(SearchBar, true);

        SearchBox.TextChanged += (_, _) =>
        {
            _hint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _clear.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RunSearch();
        };
        SearchBox.GotKeyboardFocus += (_, _) => { if (SearchText.Length > 0 && _results.Count + 1 > 0) _popup.IsOpen = true; };
        SearchBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!SearchBox.IsKeyboardFocusWithin && !Results.IsMouseOver) _popup.IsOpen = false;
        });
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                    else if (_openId is not null) Editor.Focus();
                    _popup.IsOpen = false;
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    if (_results.Count == 0) break;
                    var i = Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    Results.SelectedIndex = Math.Clamp(i, 0, _results.Count - 1);
                    Results.ScrollIntoView(Results.SelectedItem);
                    _popup.IsOpen = true;
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if ((Results.SelectedItem ?? _results.FirstOrDefault()) is NoteItem it) PickResult(it);
                    e.Handled = true;
                    break;
            }
        };
    }

    // for the self-test, which draws the drop-down without opening it on screen
    internal FrameworkElement SearchCard => (FrameworkElement)_popup.Child;
    internal void DetachSearchCard() => _popup.Child = null;
    internal void AttachSearchCard(UIElement card) => _popup.Child = card;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void PickResult(NoteItem it)
    {
        _popup.IsOpen = false;
        OpenNote(it.Id, [.. it.Terms]);
        Editor.Focus();
    }

    public void RunSearch()
    {
        var q = SearchBox.Text;
        if (q.Trim().Length == 0)
        {
            ++_searchSeq;
            _results.Clear();
            _popup.IsOpen = false;
            return;
        }
        var seq = ++_searchSeq;
        var terms = SearchEngine.Terms(q);
        Task.Run(() =>
        {
            if (seq != _searchSeq) return;
            List<SearchHit> hits;
            double ms;
            try { hits = _search.Search(q, 200, out ms); }
            catch { hits = []; ms = 0; }
            if (seq != _searchSeq) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (seq != _searchSeq) return;
                _results.Clear();
                foreach (var h in hits)
                    _results.Add(new NoteItem
                    {
                        Id = h.Id,
                        Cat = h.Cat,
                        Title = h.Title,
                        Updated = h.Updated,
                        Meta = $"{FolderPath(h.Cat)} · {When(h.Updated)}",
                        Snippet = h.Snippet,
                        Terms = terms,
                    });
                if (_results.Count > 0) Results.SelectedIndex = 0;
                _resultsInfo.Text = (hits.Count == 0 ? "Nothing found" : hits.Count == 1 ? "1 note" : hits.Count >= 200 ? "200+ notes" : $"{hits.Count} notes")
                    + $"  ·  {ms:0.0} ms" + (hits.Count > 0 ? "  ·  ↑↓ Enter to open" : "");
                if (SearchBox.IsKeyboardFocusWithin) _popup.IsOpen = true;
            });
        });
    }
}
