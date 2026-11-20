using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using pxdNotepad.Images;

namespace pxdNotepad.Editor;

/// <summary>
/// An image inside a note (the child of an InlineUIContainer). Drag the corner handle to resize it (it keeps its
/// shape); double-click or right-click → Mark up to draw arrows and boxes on it.
/// </summary>
public sealed class ImageBox : Grid
{
    public static ImageStore? Store { get; set; }

    /// <summary>Someone asked to mark this image up.</summary>
    public static event Action<ImageBox>? EditRequested;
    /// <summary>The image was resized or removed (the note has changed).</summary>
    public static event Action<ImageBox>? Changed;

    public string ImageId { get; }
    private readonly Image _img = new() { Stretch = Stretch.Fill };
    private readonly Border _frame = new() { BorderThickness = new Thickness(1.5), IsHitTestVisible = false, Opacity = 0 };
    private readonly Thumb _grip = new() { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -5), Cursor = Cursors.SizeNWSE, Opacity = 0 };
    private readonly TextBlock _missing = new() { Text = "Image not downloaded yet", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private double _aspect = 0.75;
    private int _natural;

    public ImageBox(string imageId, double width)
    {
        ImageId = imageId;
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        _frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _grip.SetResourceReference(FrameworkElement.StyleProperty, "GripThumb");
        _missing.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        Children.Add(_img);
        Children.Add(_missing);
        Children.Add(_frame);
        Children.Add(_grip);
        Refresh();
        Width = width > 0 ? width : Math.Min(_natural, 640);
        Height = Math.Round(Width * _aspect);

        _grip.DragDelta += (_, e) =>
        {
            var w = Math.Clamp(Width + e.HorizontalChange, 32, 4000);
            Width = Math.Round(w);
            Height = Math.Round(w * _aspect);
        };
        _grip.DragCompleted += (_, _) => Changed?.Invoke(this);
        MouseEnter += (_, _) => Hover(true);
        MouseLeave += (_, _) => Hover(false);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { EditRequested?.Invoke(this); e.Handled = true; }
        };
        ContextMenu = BuildMenu();
        SizeChanged += (_, _) => Sharpen();
        _img.DpiChanged += (_, _) => Sharpen();
        Loaded += (_, _) => { if (Store is not null) Store.ImageChanged += OnImageChanged; };
        Unloaded += (_, _) => { if (Store is not null) Store.ImageChanged -= OnImageChanged; };
        ToolTip = "Drag the corner to resize · double-click to mark up";
        ToolTipService.SetInitialShowDelay(this, 900);
    }

    public double DisplayWidth => Width;

    private void Hover(bool on)
    {
        _frame.Opacity = on ? 0.9 : 0;
        _grip.Opacity = on ? 1 : 0;
    }

    private void OnImageChanged(string id)
    {
        if (id != ImageId) return;
        Refresh();
    }

    public void Refresh()
    {
        var info = Store?.Info(ImageId);
        if (info is not null && info.Width > 0)
        {
            _aspect = (double)info.Height / info.Width;
            _natural = info.Width;
        }
        else _natural = 320;
        // the same width in the note, the picture's own shape (an edit that cut a band out makes it shorter or narrower)
        if (!double.IsNaN(Width) && Width > 0) Height = Math.Round(Width * _aspect);
        var bmp = Store?.Bitmap(ImageId, Needed());
        _img.Source = bmp;
        _missing.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
        if (bmp is null) SetResourceReference(BackgroundProperty, "Panel2");
        else Background = Brushes.Transparent;
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string text, Action a)
        {
            var i = new MenuItem { Header = text };
            i.Click += (_, _) => a();
            m.Items.Add(i);
            return i;
        }
        Item("Mark up…", () => EditRequested?.Invoke(this));
        m.Items.Add(new Separator());
        Item("Actual size", () => SetWidth(_natural));
        Item("Half size", () => SetWidth(_natural / 2.0));
        Item("Fit to page width", () =>
        {
            var rtb = FindEditor();
            if (rtb is not null) SetWidth(Math.Max(64, rtb.ActualWidth - rtb.Padding.Left - rtb.Padding.Right - 30));
        });
        m.Items.Add(new Separator());
        Item("Copy image", () =>
        {
            if (Store?.Bitmap(ImageId) is { } b) Clipboard.SetImage(b);
        });
        Item("Remove from note", () =>
        {
            if (Parent is InlineUIContainer c)
            {
                if (c.Parent is Paragraph p) p.Inlines.Remove(c);
                else if (c.Parent is Span s) s.Inlines.Remove(c);
                Changed?.Invoke(this);
            }
        });
        return m;
    }

    /// <summary>The pixels it's shown at on this screen (it's decoded no bigger).</summary>
    private int Needed()
    {
        var w = double.IsNaN(Width) || Width <= 0 ? Math.Min(_natural > 0 ? _natural : 640, 640) : Width;
        return (int)Math.Ceiling(w * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>Made bigger than its picture was decoded for: decode it again at the new size (once resizing stops).</summary>
    private void Sharpen()
    {
        if (_img.Source is not System.Windows.Media.Imaging.BitmapSource b || b.PixelWidth >= Needed() || b.PixelWidth >= Math.Min(_natural, ImageStore.MaxDecode)) return;
        _sharpen ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _sharpen!.Stop();
            if (Store?.Bitmap(ImageId, Needed()) is { } nb) _img.Source = nb;
        }, Dispatcher);
        _sharpen.Stop();
        _sharpen.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _sharpen;

    public void SetWidth(double w)
    {
        Width = Math.Round(Math.Clamp(w, 32, 4000));
        Height = Math.Round(Width * _aspect);
        Changed?.Invoke(this);
    }

    private RichTextBox? FindEditor()
    {
        DependencyObject? d = this;
        while (d is not null and not RichTextBox)
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as RichTextBox;
    }
}
