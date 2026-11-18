// pxd App Standard rules 1-3 for a WPF app (the canonical Avalonia copy is pxdStandard.cs in pxdtek/pxdToolkit):
// - Chrome.Apply: the app's own 38 px title bar (icon, title, minimize / maximize / close, close red on hover),
//   drag + double-click maximize, Windows snap kept (WindowChrome, not WindowStyle=None).
// - Chrome.RememberPlacement: window size, position and maximized state in <data>\windows.json, ignored off screen.
// - SingleInstance: a second start hands over to the first (which shows its window) and exits.
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Shell;

namespace pxdStandard;

public static class Chrome
{
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    public static string DataDir { get; set; } = "";

    /// <summary>The title bar Apply made, so a window can make it slimmer (or back) later.</summary>
    public sealed class Bar
    {
        internal Window Window = null!;
        internal Grid Grid = null!;
        internal readonly List<FrameworkElement> Sized = [];
        public TextBlock Title { get; internal set; } = null!;
        public double Height => Grid.Height;

        /// <summary>Minimize / maximize / close width (narrower in a slim bar).</summary>
        public void SetButtonWidth(double w)
        {
            foreach (var b in Sized) b.Width = w;
        }

        public void SetHeight(double h)
        {
            Grid.Height = h;
            foreach (var b in Sized) b.Height = h;
            if (WindowChrome.GetWindowChrome(Window) is { } c) c.CaptionHeight = h;
        }
    }

    /// <summary>Puts the pxd title bar above <paramref name="content"/> and makes it the window's content.
    /// <paramref name="center"/> (optional) sits in the middle of the bar, e.g. a search box.</summary>
    public static Bar Apply(Window w, string title, UIElement content, ImageSource? icon, bool resizable = true, UIElement? extras = null, UIElement? center = null)
    {
        var handle = new Bar { Window = w };
        w.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.CanMinimize;
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 38,
            ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        w.SetResourceReference(Window.ForegroundProperty, "Text");
        if (icon is not null) w.Icon = icon;
        w.Title = title;

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
            left.Children.Add(new Image { Source = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 9, 0) });
        var name = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TitleText");
        left.Children.Add(name);
        handle.Title = name;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (extras is not null)
        {
            WindowChrome.SetIsHitTestVisibleInChrome(extras, true);
            buttons.Children.Add(extras);
        }
        var min = Caption("M0,5.5 L10,5.5", "Minimize", () => w.WindowState = WindowState.Minimized);
        handle.Sized.Add(min);
        buttons.Children.Add(min);
        Path? maxGlyph = null;
        if (resizable)
        {
            var max = Caption(MaximizeGlyph, "Maximize", () => Toggle(w));
            maxGlyph = (Path)max.Content;
            handle.Sized.Add(max);
            buttons.Children.Add(max);
        }
        var close = Caption("M0,0 L10,10 M10,0 L0,10", "Close", w.Close, close: true);
        handle.Sized.Add(close);
        buttons.Children.Add(close);

        var bar = new Grid { Height = 38 };
        handle.Grid = bar;
        bar.SetResourceReference(Panel.BackgroundProperty, "TitleBg");
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ClipToBounds = true;
        bar.Children.Add(left);
        if (center is not null)
        {
            WindowChrome.SetIsHitTestVisibleInChrome(center, true);
            if (center is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(center, 1);
            bar.Children.Add(center);
        }
        Grid.SetColumn(buttons, 2);
        bar.Children.Add(buttons);

        var edge = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        edge.SetResourceReference(Border.BorderBrushProperty, "TitleLine");
        DockPanel.SetDock(edge, Dock.Top);
        var dock = new DockPanel();
        dock.Children.Add(edge);
        dock.Children.Add(content);

        var frame = new Border { Child = dock, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "TitleLine");
        w.Content = frame;

        void StateChanged()
        {
            var m = w.WindowState == WindowState.Maximized;
            // A maximized WindowChrome window hangs over the screen edge by the resize frame: pad it back in.
            frame.Margin = m ? MaximizedInset(w) : default;
            frame.BorderThickness = m ? default : new Thickness(1);
            if (maxGlyph is not null) maxGlyph.Data = Geometry.Parse(m ? RestoreGlyph : MaximizeGlyph);
        }
        w.StateChanged += (_, _) => StateChanged();
        w.SourceInitialized += (_, _) =>
        {
            StateChanged();
            RoundCorners(w);
        };
        return handle;
    }

    private static void Toggle(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static Button Caption(string glyph, string tip, Action click, bool close = false)
    {
        var path = new Path { Data = Geometry.Parse(glyph), Width = 10, Height = 10, StrokeThickness = 1, Stretch = Stretch.None, SnapsToDevicePixels = true };
        path.SetResourceReference(Shape.StrokeProperty, "TitleText");
        var b = new Button { Content = path, Width = 38, Height = 38, ToolTip = tip, Focusable = false };
        b.SetResourceReference(FrameworkElement.StyleProperty, close ? "CaptionCloseButton" : "CaptionButton");
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => click();
        return b;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static Thickness MaximizedInset(Window w)
    {
        var scale = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var px = GetSystemMetrics(32 /* SM_CXSIZEFRAME */) + GetSystemMetrics(92 /* SM_CXPADDEDBORDER */);
        return new Thickness(px / scale);
    }

    private static void RoundCorners(Window w)
    {
        try
        {
            var pref = 2;   // DWMWCP_ROUND (Windows 11; ignored before)
            DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 33, ref pref, sizeof(int));
        }
        catch
        {
            /* older Windows */
        }
    }

    // ── remembered placement ──

    private sealed class Placement
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string PlacementFile => System.IO.Path.Combine(DataDir, "windows.json");

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    /// <summary>Restores and keeps saving the window's placement under <paramref name="key"/>. Call before Show.</summary>
    public static PlacementTracker RememberPlacement(Window w, string key) => new(w, key);

    /// <summary>
    /// Keeps a window's size, position and maximized state saved under a key, and can switch to another key (e.g. a
    /// second layout of the same window): the current spot is saved, then the window goes to the other key's spot in
    /// one move. A key with nothing saved yet keeps the window where it is.
    /// </summary>
    public sealed class PlacementTracker
    {
        private readonly Window _w;
        private Placement _cur = new();
        private bool _ready, _moving;
        public string Key { get; private set; }

        internal PlacementTracker(Window w, string key)
        {
            _w = w;
            Key = key;
            if (LoadPlacements().TryGetValue(key, out var p))
            {
                _cur = Clone(p);
                if (w.ResizeMode == ResizeMode.CanResize && p.Width > 100 && p.Height > 80)
                {
                    w.Width = Math.Max(w.MinWidth, p.Width);
                    w.Height = Math.Max(w.MinHeight, p.Height);
                }
                if (OnScreen(p))
                {
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = p.X;
                    w.Top = p.Y;
                }
                if (p.Maximized && w.ResizeMode == ResizeMode.CanResize)
                    w.WindowState = WindowState.Maximized;
            }
            w.ContentRendered += (_, _) => { _ready = true; Grab(); };
            w.LocationChanged += (_, _) => { if (Grab()) Save(); };
            w.SizeChanged += (_, _) => { if (Grab()) Save(); };
            w.StateChanged += (_, _) =>
            {
                if (_moving || w.WindowState == WindowState.Minimized) return;
                _cur.Maximized = w.WindowState == WindowState.Maximized;
                Save();
            };
        }

        /// <summary>Whether <paramref name="key"/> has a saved spot.</summary>
        public static bool Has(string key) => LoadPlacements().ContainsKey(key);

        /// <summary>
        /// Saves where the window is under the current key, then moves it to <paramref name="key"/>'s spot (size,
        /// position, maximized) in one step. <paramref name="beforeMove"/> runs just before the move (e.g. to set new
        /// minimum sizes), so nothing jumps twice.
        /// </summary>
        public void SwitchTo(string key, Action? beforeMove = null, Action? afterMove = null)
        {
            if (key == Key) { beforeMove?.Invoke(); afterMove?.Invoke(); return; }
            Grab();
            Save();
            Key = key;
            var saved = LoadPlacements().TryGetValue(key, out var p) ? p : null;
            _moving = true;
            try
            {
                if (saved is null)
                {
                    // First time in this layout: stay put; from now on it's remembered.
                    beforeMove?.Invoke();
                    _cur = Clone(_cur);
                }
                else
                {
                    _cur = Clone(saved);
                    if (_w.WindowState == WindowState.Maximized && !saved.Maximized)
                        _w.WindowState = WindowState.Normal;
                    beforeMove?.Invoke();
                    if (_w.WindowState != WindowState.Maximized || saved.Maximized)
                        MoveTo(saved);
                    if (saved.Maximized && _w.WindowState != WindowState.Maximized)
                        _w.WindowState = WindowState.Maximized;
                }
            }
            finally
            {
                _moving = false;
            }
            afterMove?.Invoke();
            Grab();
            Save();
        }

        /// <summary>Size and position in one SetWindowPos (no in-between frame), kept on a screen that's still there.</summary>
        private void MoveTo(Placement p)
        {
            var w = Math.Max(_w.MinWidth, p.Width);
            var h = Math.Max(_w.MinHeight, p.Height);
            var x = OnScreen(p) ? p.X : _w.Left;
            var y = OnScreen(p) ? p.Y : _w.Top;
            var hwnd = new WindowInteropHelper(_w).Handle;
            if (hwnd == IntPtr.Zero)
            {
                _w.Left = x; _w.Top = y; _w.Width = w; _w.Height = h;
                return;
            }
            var s = GetDpiForSystem() / 96.0;
            SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x * s), (int)Math.Round(y * s), (int)Math.Round(w * s), (int)Math.Round(h * s), 0x0004 | 0x0010 /* NOZORDER | NOACTIVATE */);
        }

        private bool Grab()
        {
            if (_moving || _w.WindowState != WindowState.Normal || _w.ActualWidth < 50) return false;
            _cur.X = _w.Left;
            _cur.Y = _w.Top;
            _cur.Width = _w.ActualWidth;
            _cur.Height = _w.ActualHeight;
            return true;
        }

        private void Save()
        {
            if (!_ready || _moving) return;
            var latest = LoadPlacements();
            latest[Key] = Clone(_cur);
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(PlacementFile, JsonSerializer.Serialize(latest));
            }
            catch
            {
                /* next move tries again */
            }
        }

        private static Placement Clone(Placement p) => new() { X = p.X, Y = p.Y, Width = p.Width, Height = p.Height, Maximized = p.Maximized };
    }

    /// <summary>The title bar's left part is on a screen that's still connected.</summary>
    private static bool OnScreen(Placement p)
    {
        var scale = GetDpiForSystem() / 96.0;
        return MonitorFromPoint(new POINT { X = (int)((p.X + 60) * scale), Y = (int)((p.Y + 15) * scale) }, 0) != IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static Dictionary<string, Placement> LoadPlacements()
    {
        try
        {
            if (File.Exists(PlacementFile))
                return JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllText(PlacementFile)) ?? [];
        }
        catch
        {
            /* damaged: start fresh */
        }
        return [];
    }

    /// <summary>Brings a window to the front (restoring it when minimized).</summary>
    public static void BringToFront(Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }
}

/// <summary>One copy per user: a second start signals the first (which brings its window up) and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle? _wait;
    public bool IsFirst { get; }
    public event Action? ShowRequested;
    /// <summary>A second start was given a file to open (e.g. a .txt double-clicked in Explorer).</summary>
    public event Action<string>? FileRequested;
    private readonly string _pipe;

    public SingleInstance(string appId)
    {
        _mutex = new Mutex(true, $@"Local\{appId}-single", out var created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{appId}-show");
        _pipe = $"{appId}-open-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        if (IsFirst)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => ShowRequested?.Invoke(), null, -1, false);
            new Thread(Listen) { IsBackground = true, Name = "open-files" }.Start();
        }
    }

    /// <summary>Waits for files sent by later starts (one path per connection).</summary>
    private void Listen()
    {
        while (true)
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(_pipe, System.IO.Pipes.PipeDirection.In, 1);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                var path = reader.ReadToEnd().Trim();
                if (path.Length > 0) FileRequested?.Invoke(path);
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    public void SignalFirst() => _show.Set();

    /// <summary>Sends a file to the copy that's already running (then brings it up).</summary>
    public void SignalFirst(string? file)
    {
        if (file is not null)
            try
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(".", _pipe, System.IO.Pipes.PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client);
                writer.Write(file);
                writer.Flush();
            }
            catch
            {
                /* the first copy at least comes up */
            }
        _show.Set();
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (IsFirst)
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
        _show.Dispose();
    }
}
