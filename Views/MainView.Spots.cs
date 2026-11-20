using System.Globalization;
using System.Windows.Documents;
using System.Windows.Threading;

namespace pxdNotepad.Views;

/// <summary>
/// Where you left off: each note reopens on the tab you were on, with the caret where it was and scrolled as it was,
/// and each tab keeps its own place while you move between tabs. Kept on this PC (settings.json), for the last 300 notes.
/// </summary>
public partial class MainView
{
    private const int SpotsKept = 300;

    /// <summary>The shown tab's caret and scroll, kept on the tab as it's left.</summary>
    private void KeepTabSpot()
    {
        if (_tab < 0 || _tab >= _tabs.Count || Editor.Document != _tabs[_tab].Doc) return;
        _tabs[_tab].Scroll = Editor.VerticalOffset;
        _tabs[_tab].Caret = Editor.CaretPosition;
    }

    /// <summary>Notes where the open note was left (its tab, and the caret and scroll in each tab) for next time.</summary>
    private void RememberSpot()
    {
        if (_openId is null || _file is not null || _tabs.Count == 0) return;
        KeepTabSpot();
        var parts = new List<string> { _tab.ToString(CultureInfo.InvariantCulture) };
        foreach (var t in _tabs)
        {
            var caret = t.Caret is { } c && c.IsInSameDocument(t.Doc.ContentStart) ? t.Doc.ContentStart.GetOffsetToPosition(c) : 0;
            parts.Add(caret.ToString(CultureInfo.InvariantCulture) + "," + Math.Round(t.Scroll).ToString(CultureInfo.InvariantCulture));
        }
        _settings.Spots.Remove(_openId);   // re-added last: the oldest are first in line to go
        _settings.Spots[_openId] = string.Join(';', parts);
        while (_settings.Spots.Count > SpotsKept) _settings.Spots.Remove(_settings.Spots.Keys.First());
    }

    /// <summary>Puts the just-loaded tabs back where they were left: which tab, and each tab's caret and scroll.</summary>
    private void ApplySpot(string id)
    {
        if (!_settings.Spots.TryGetValue(id, out var spot)) return;
        var parts = spot.Split(';');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tab)) return;
        for (var i = 0; i < _tabs.Count && i + 1 < parts.Length; i++)
        {
            var cs = parts[i + 1].Split(',');
            if (cs.Length != 2
                || !int.TryParse(cs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var caret)
                || !double.TryParse(cs[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scroll)) continue;
            var doc = _tabs[i].Doc;
            // the note may have changed since (another PC): never past its end
            caret = Math.Clamp(caret, 0, doc.ContentStart.GetOffsetToPosition(doc.ContentEnd));
            _tabs[i].Caret = doc.ContentStart.GetPositionAtOffset(caret)?.GetInsertionPosition(LogicalDirection.Forward);
            _tabs[i].Scroll = Math.Max(0, scroll);
        }
        _tab = Math.Clamp(tab, 0, _tabs.Count - 1);
    }

    /// <summary>The shown tab's caret and scroll put back (the scroll once its text is laid out, so a long page can get there).</summary>
    private void RestoreTabSpot()
    {
        if (_tab < 0 || _tab >= _tabs.Count) return;
        var t = _tabs[_tab];
        if (t.Caret is { } c && c.IsInSameDocument(Editor.Document.ContentStart)) Editor.CaretPosition = c;
        else Editor.CaretPosition = Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var offset = t.Scroll;
        Editor.ScrollToVerticalOffset(offset);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Editor.Document != t.Doc) return;
            Editor.UpdateLayout();
            Editor.ScrollToVerticalOffset(offset);
        });
    }
}
