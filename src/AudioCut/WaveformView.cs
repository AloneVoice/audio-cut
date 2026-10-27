using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioCut;

/// <summary>
/// Zoomable waveform of an edited <see cref="Timeline"/> with a draggable selection and a draggable playhead.
/// All times are timeline times (after cuts), except the peak data which is indexed by source time.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public const int PeaksPerSecond = 200;
    private const double RulerHeight = 30;
    private const double EdgeGrab = 7;
    private const double MinSpan = 1.0;

    // peak data in source time: [min0, max0, min1, max1, ...]
    private float[] _peaks = new float[4096];
    private int _peakCount;
    private float _peakMax;

    public Timeline Timeline { get; private set; } = Timeline.Full(0);
    public double Duration => Timeline.Duration;
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; }
    public double SelStart { get; private set; }
    public double SelEnd { get; private set; }
    public double Playhead { get; private set; }
    public bool HasData => Duration > 0;
    public bool IsLoadingPeaks { get; set; }
    public bool IsDraggingPlayhead => _drag == Drag.Playhead;
    /// <summary>Loudest absolute sample of the source (0..1); available once peaks are loaded.</summary>
    public float PeakMax => _peakMax;

    private double _gain = 1;
    /// <summary>Volume multiplier shown in the waveform (1 = original).</summary>
    public double Gain
    {
        get => _gain;
        set { _gain = value; _waveCache = null; InvalidateVisual(); }
    }

    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    /// <summary>Selection was changed by the user and the gesture is finished (for undo).</summary>
    public event Action? SelectionCommitted;
    /// <summary>User clicked or released the playhead somewhere (time in seconds).</summary>
    public event Action<double>? SeekRequested;
    /// <summary>Playhead is being dragged.</summary>
    public event Action<double>? PlayheadDragged;

    // colours
    private static readonly Color Teal = Color.FromRgb(0x4F, 0xD1, 0xC5);
    private static readonly Color Violet = Color.FromRgb(0x7C, 0x8C, 0xF8);
    private static readonly Brush BgBrush = Frozen(Color.FromRgb(0x17, 0x1A, 0x21));
    private static readonly Brush RulerBrush = Frozen(Color.FromRgb(0x1E, 0x22, 0x2B));
    private static readonly Brush RulerHoverBrush = Frozen(Color.FromRgb(0x23, 0x28, 0x33));
    private static readonly Brush WaveDim = Frozen(Color.FromRgb(0x4A, 0x52, 0x63));
    private static readonly Brush ClipBrush = Frozen(Color.FromRgb(0xFF, 0x5A, 0x6A));
    private static readonly Brush TickText = Frozen(Color.FromRgb(0x8A, 0x93, 0xA6));
    private static readonly Brush HintText = Frozen(Color.FromRgb(0x6A, 0x73, 0x86));
    private static readonly Brush PillBrush = Frozen(Color.FromArgb(0xE8, 0x30, 0x36, 0x44));
    private static readonly Brush PlayheadPill = Frozen(Colors.White);
    private static readonly Brush PlayheadText = Frozen(Color.FromRgb(0x11, 0x13, 0x18));
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x3A, 0x41, 0x50), 1);
    private static readonly Pen CenterPen = FrozenPen(Color.FromRgb(0x2A, 0x30, 0x3C), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Colors.White, 1.5);
    private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen GripPen = FrozenPen(Color.FromArgb(0x90, 0x0B, 0x1A, 0x22), 1);
    private static readonly Pen JoinPen = MakeJoinPen();
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private enum Drag { None, Pending, NewSel, Start, End, Pan, Playhead }
    private Drag _drag;
    private Point _downPt;
    private double _downTime, _panViewStart;
    private Point? _hover;

    private StreamGeometry? _waveCache;
    private StreamGeometry? _clipCache;
    private (double, double, double, double, int, Timeline?) _waveKey;

    public WaveformView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
    }

    // ---------- data ----------

    public void Reset(double duration)
    {
        _peakCount = 0;
        _peakMax = 0;
        _waveCache = null;
        Timeline = Timeline.Full(duration);
        ViewStart = 0; ViewEnd = Duration;
        SelStart = 0; SelEnd = Duration;
        Playhead = 0;
        InvalidateVisual();
        ViewChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void AddPeaks(float[] chunk, int pairs)
    {
        int need = (_peakCount + pairs) * 2;
        if (need > _peaks.Length) Array.Resize(ref _peaks, Math.Max(need, _peaks.Length * 2));
        Array.Copy(chunk, 0, _peaks, _peakCount * 2, pairs * 2);
        for (int i = 0; i < pairs * 2; i++) _peakMax = Math.Max(_peakMax, Math.Abs(chunk[i]));
        _peakCount += pairs;
        InvalidateVisual();
    }

    /// <summary>Called when decoding is done; fixes duration if the container did not report it.</summary>
    public void FinishPeaks()
    {
        double real = _peakCount / (double)PeaksPerSecond;
        if (Duration <= 0 && real > 0)
        {
            Timeline = Timeline.Full(real);
            ViewStart = 0; ViewEnd = real;
            SelStart = 0; SelEnd = real;
            ViewChanged?.Invoke();
            SelectionChanged?.Invoke();
        }
        InvalidateVisual();
    }

    /// <summary>Replaces the edit (trim / delete / undo). Keeps the view as close as possible.</summary>
    public void SetTimeline(Timeline t, double selStart, double selEnd)
    {
        Timeline = t;
        _waveCache = null;
        double span = Math.Min(ViewEnd - ViewStart, Duration);
        if (ViewStart + span > Duration || span <= 0) SetView(0, Duration, notify: false);
        else SetView(ViewStart, ViewStart + span, notify: false);
        Playhead = Math.Clamp(Playhead, 0, Duration);
        SetSelection(selStart, selEnd, notify: false);
        ViewChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void SetSelection(double a, double b, bool notify = true)
    {
        if (b < a) (a, b) = (b, a);
        SelStart = Math.Clamp(a, 0, Duration);
        SelEnd = Math.Clamp(b, 0, Duration);
        InvalidateVisual();
        if (notify) SelectionChanged?.Invoke();
    }

    public void SetPlayhead(double t)
    {
        Playhead = Math.Clamp(t, 0, Duration);
        InvalidateVisual();
    }

    public void SetView(double start, double end, bool notify = true)
    {
        double span = Math.Clamp(end - start, Math.Min(MinSpan, Duration), Duration);
        start = Math.Clamp(start, 0, Math.Max(0, Duration - span));
        ViewStart = start; ViewEnd = start + span;
        InvalidateVisual();
        if (notify) ViewChanged?.Invoke();
    }

    public void ZoomAt(double time, double factor)
    {
        if (!HasData) return;
        double span = ViewEnd - ViewStart;
        double rel = (time - ViewStart) / span;
        double newSpan = Math.Clamp(span * factor, Math.Min(MinSpan, Duration), Duration);
        SetView(time - rel * newSpan, time - rel * newSpan + newSpan);
    }

    public void ZoomToSelection()
    {
        if (SelEnd - SelStart <= 0) return;
        // keep the selection centred even when it is shorter than the minimum zoom span
        double span = Math.Max((SelEnd - SelStart) * 1.16, Math.Min(MinSpan, Duration));
        double centre = (SelStart + SelEnd) / 2;
        SetView(centre - span / 2, centre + span / 2);
    }

    /// <summary>Scrolls so that <paramref name="t"/> is visible (page-style, used while playing).</summary>
    public void EnsureVisible(double t)
    {
        double span = ViewEnd - ViewStart;
        if (t < ViewStart || t > ViewEnd) SetView(t - span * 0.05, t - span * 0.05 + span);
    }

    // ---------- coordinates ----------

    private double TimeToX(double t) => (t - ViewStart) / (ViewEnd - ViewStart) * ActualWidth;
    private double XToTime(double x) => Math.Clamp(ViewStart + x / Math.Max(1, ActualWidth) * (ViewEnd - ViewStart), 0, Duration);

    // ---------- rendering ----------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 12, 12));
        try { RenderContent(dc, w, h); }
        finally { dc.Pop(); }
    }

    private void RenderContent(DrawingContext dc, double w, double h)
    {
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (!HasData || w < 2 || ViewEnd <= ViewStart) return;

        var waveRect = new Rect(0, RulerHeight, w, h - RulerHeight);
        double mid = waveRect.Top + waveRect.Height / 2;

        bool rulerHover = _hover is Point hp && hp.Y < RulerHeight || _drag == Drag.Playhead;
        dc.DrawRectangle(rulerHover ? RulerHoverBrush : RulerBrush, null, new Rect(0, 0, w, RulerHeight));
        DrawRuler(dc, w);
        dc.DrawLine(CenterPen, new Point(0, mid), new Point(w, mid));

        double sx0 = TimeToX(SelStart), sx1 = TimeToX(SelEnd);
        bool hasSel = SelEnd - SelStart > 0.0005;
        var waveSel = new LinearGradientBrush(Teal, Violet, new Point(sx0, 0), new Point(Math.Max(sx1, sx0 + 1), 0)) { MappingMode = BrushMappingMode.Absolute };
        var selFill = new LinearGradientBrush(Color.FromArgb(0x22, Teal.R, Teal.G, Teal.B), Color.FromArgb(0x22, Violet.R, Violet.G, Violet.B),
                                              new Point(sx0, 0), new Point(Math.Max(sx1, sx0 + 1), 0)) { MappingMode = BrushMappingMode.Absolute };
        if (hasSel)
            dc.DrawRectangle(selFill, null, new Rect(new Point(Math.Max(0, sx0), RulerHeight), new Point(Math.Min(w, sx1), h)));

        var geo = GetWaveGeometry(w, waveRect);
        if (geo != null)
        {
            dc.DrawGeometry(WaveDim, null, geo);
            if (hasSel)
            {
                dc.PushClip(new RectangleGeometry(new Rect(new Point(Math.Max(0, sx0), 0), new Point(Math.Min(w, sx1), h))));
                dc.DrawGeometry(waveSel, null, geo);
                dc.Pop();
            }
        }

        if (geo != null && _clipCache != null) dc.DrawGeometry(ClipBrush, null, _clipCache);

        // cut markers
        foreach (var j in Timeline.Joins())
        {
            double jx = TimeToX(j);
            if (jx < -2 || jx > w + 2) continue;
            dc.DrawLine(JoinPen, new Point(jx, RulerHeight), new Point(jx, h));
            var mark = new StreamGeometry();
            using (var g = mark.Open())
            {
                g.BeginFigure(new Point(jx - 4, h), true, true);
                g.LineTo(new Point(jx + 4, h), true, false);
                g.LineTo(new Point(jx, h - 6), true, false);
            }
            dc.DrawGeometry(Brushes.Orange, null, mark);
        }

        if (IsLoadingPeaks && Timeline.IsOriginal)
        {
            double loadedX = TimeToX(_peakCount / (double)PeaksPerSecond);
            var ft = Text("Analizuję dźwięk…", 12, TickText, Face);
            double lx = Math.Max(loadedX + 10, 10);
            if (lx + ft.Width < w) dc.DrawText(ft, new Point(lx, mid - ft.Height / 2));
        }
        else if (Timeline.IsOriginal && SelStart <= 0 && SelEnd >= Duration && _drag == Drag.None)
        {
            var ft = Text("Przeciągnij po fali, żeby zaznaczyć fragment", 12, HintText, Face);
            dc.DrawRoundedRectangle(PillBrush, null, new Rect(12, h - ft.Height - 18, ft.Width + 20, ft.Height + 8), 6, 6);
            dc.DrawText(ft, new Point(22, h - ft.Height - 14));
        }

        if (hasSel)
        {
            DrawHandle(dc, sx0, h, Teal);
            DrawHandle(dc, sx1, h, Violet);
        }

        if (_hover is Point hv && _drag == Drag.None)
        {
            double hx = hv.X;
            dc.DrawLine(HoverPen, new Point(hx, RulerHeight), new Point(hx, h));
            DrawPill(dc, TimeFormat.Short(XToTime(hx)), hx, w, PillBrush, Brushes.White);
        }

        DrawPlayhead(dc, w, h);
    }

    private void DrawPlayhead(DrawingContext dc, double w, double h)
    {
        double px = TimeToX(Playhead);
        if (px < -10 || px > w + 10) return;
        dc.DrawLine(PlayheadPen, new Point(px, RulerHeight - 2), new Point(px, h));

        // big grabbable head
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            double top = 3, hw = 8, body = RulerHeight - 11;
            g.BeginFigure(new Point(px - hw + 2, top), true, true);
            g.LineTo(new Point(px + hw - 2, top), true, true);
            g.ArcTo(new Point(px + hw, top + 2), new Size(2, 2), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(px + hw, body), true, true);
            g.LineTo(new Point(px, RulerHeight - 1), true, true);
            g.LineTo(new Point(px - hw, body), true, true);
            g.LineTo(new Point(px - hw, top + 2), true, true);
            g.ArcTo(new Point(px - hw + 2, top), new Size(2, 2), 0, false, SweepDirection.Clockwise, true, true);
        }
        head.Freeze();
        dc.DrawGeometry(Brushes.White, null, head);

        if (_drag == Drag.Playhead)
            DrawPill(dc, TimeFormat.Full(Playhead), px, w, PlayheadPill, PlayheadText, offsetX: 14);
    }

    private void DrawPill(DrawingContext dc, string s, double x, double w, Brush bg, Brush fg, double offsetX = 0)
    {
        var ft = Text(s, 11, fg, FaceBold);
        double pw = ft.Width + 12;
        double tx = offsetX == 0 ? x - pw / 2 : x + offsetX;
        if (tx + pw > w) tx = offsetX == 0 ? w - pw : x - offsetX - pw;
        tx = Math.Max(0, tx);
        dc.DrawRoundedRectangle(bg, null, new Rect(tx, 5, pw, RulerHeight - 10), 5, 5);
        dc.DrawText(ft, new Point(tx + 6, (RulerHeight - ft.Height) / 2));
    }

    private static void DrawHandle(DrawingContext dc, double x, double h, Color c)
    {
        dc.DrawLine(FrozenPen(c, 2), new Point(x, RulerHeight), new Point(x, h));
        double gw = 10, gh = 28;
        double y = RulerHeight + (h - RulerHeight - gh) / 2;
        var r = new Rect(x - gw / 2, y, gw, gh);
        dc.DrawRoundedRectangle(Frozen(c), null, r, 5, 5);
        dc.DrawLine(GripPen, new Point(x - 1.5, y + 8), new Point(x - 1.5, y + gh - 8));
        dc.DrawLine(GripPen, new Point(x + 1.5, y + 8), new Point(x + 1.5, y + gh - 8));
    }

    /// <summary>Normalises quiet recordings for display, then applies the gain.</summary>
    private double DisplayScale() => _gain / Math.Clamp(_peakMax, 0.05f, 1f);

    private StreamGeometry? GetWaveGeometry(double w, Rect area)
    {
        var key = (ViewStart, ViewEnd, w, area.Height, _peakCount, (Timeline?)Timeline);
        if (_waveCache != null && _waveKey == key) return _waveCache;
        if (_peakCount == 0) return null;

        int cols = (int)Math.Ceiling(w);
        var clipped = new List<int>();
        var top = new double[cols];
        var bot = new double[cols];
        double mid = area.Top + area.Height / 2;
        double half = area.Height / 2;
        double amp = half * 0.92 * DisplayScale();
        double pps = PeaksPerSecond;
        double colSpan = (ViewEnd - ViewStart) / w;
        var pieces = Timeline.Pieces(ViewStart, ViewEnd);
        int pi = 0;
        int last = -1;

        for (int c = 0; c < cols; c++)
        {
            double t0 = ViewStart + c * colSpan, t1 = t0 + colSpan;
            float mn = 0, mx = 0;
            bool any = false;
            while (pi < pieces.Count && pieces[pi].At + pieces[pi].Length <= t0) pi++;
            for (int k = pi; k < pieces.Count && pieces[k].At < t1; k++)
            {
                var p = pieces[k];
                double s0 = p.SourceStart + Math.Max(0, t0 - p.At);
                double s1 = p.SourceStart + Math.Min(p.Length, t1 - p.At);
                int i0 = (int)(s0 * pps), i1 = Math.Max(i0 + 1, (int)Math.Ceiling(s1 * pps));
                i1 = Math.Min(i1, _peakCount);
                for (int i = i0; i < i1; i++)
                {
                    float a = _peaks[i * 2], b = _peaks[i * 2 + 1];
                    if (a < mn) mn = a;
                    if (b > mx) mx = b;
                    any = true;
                }
            }
            if (!any) { if (last >= 0 && IsLoadingPeaks) break; top[c] = bot[c] = mid; continue; }
            if (Math.Max(mx, -mn) * _gain > 1.0) clipped.Add(c);
            top[c] = mid - Math.Min(Math.Max(mx * amp, 0.5), half);
            bot[c] = mid - Math.Max(Math.Min(mn * amp, -0.5), -half);
            last = c;
        }
        if (last < 0) return null;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, top[0]), true, true);
            for (int c = 1; c <= last; c++) g.LineTo(new Point(c + 0.5, top[c]), false, false);
            g.LineTo(new Point(last + 1, top[last]), false, false);
            g.LineTo(new Point(last + 1, bot[last]), false, false);
            for (int c = last; c >= 0; c--) g.LineTo(new Point(c + 0.5, bot[c]), false, false);
            g.LineTo(new Point(0, bot[0]), false, false);
        }
        geo.Freeze();
        _clipCache = null;
        if (clipped.Count > 0)
        {
            var cg = new StreamGeometry();
            using (var g = cg.Open())
                foreach (var c in clipped)
                {
                    AddRect(g, new Rect(c, area.Top, 1, 4));
                    AddRect(g, new Rect(c, area.Bottom - 4, 1, 4));
                }
            cg.Freeze();
            _clipCache = cg;
        }
        _waveCache = geo;
        _waveKey = key;
        return geo;
    }

    private static void AddRect(StreamGeometryContext g, Rect r)
    {
        g.BeginFigure(r.TopLeft, true, true);
        g.LineTo(r.TopRight, false, false);
        g.LineTo(r.BottomRight, false, false);
        g.LineTo(r.BottomLeft, false, false);
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        double span = ViewEnd - ViewStart;
        double[] steps = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        double step = steps[^1];
        foreach (var s in steps)
            if (s / span * w >= 80) { step = s; break; }
        double minor = step / 5;

        double first = Math.Floor(ViewStart / minor) * minor;
        for (double t = first; t <= ViewEnd; t += minor)
        {
            double x = Math.Round(TimeToX(t)) + 0.5;
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            dc.DrawLine(TickPen, new Point(x, RulerHeight - (major ? 9 : 4)), new Point(x, RulerHeight));
            if (major)
            {
                var ft = Text(TimeFormat.Ruler(t, step), 10.5, TickText, Face);
                dc.DrawText(ft, new Point(x + 4, 6));
            }
        }
    }

    private FormattedText Text(string s, double size, Brush b, Typeface f) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, f, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---------- input ----------

    /// <summary>What a press at <paramref name="p"/> would grab. The ruler always means "playhead".</summary>
    private Drag HitTest(Point p)
    {
        if (p.Y < RulerHeight) return Drag.Playhead;
        if (SelEnd - SelStart > 0.0005)
        {
            double a = TimeToX(SelStart), b = TimeToX(SelEnd);
            double da = Math.Abs(p.X - a), db = Math.Abs(p.X - b);
            if (da <= EdgeGrab || db <= EdgeGrab) return da <= db ? Drag.Start : Drag.End;
        }
        if (Math.Abs(p.X - TimeToX(Playhead)) <= 4) return Drag.Playhead;
        return Drag.None;
    }

    private Cursor CursorFor(Drag d) => d switch
    {
        Drag.Playhead => Cursors.Hand,
        Drag.Start or Drag.End => Cursors.SizeWE,
        _ => Cursors.IBeam,
    };

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!HasData) return;
        Focus();
        var pt = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.Control))
        {
            _drag = Drag.Pan; _downPt = pt; _panViewStart = ViewStart;
            Cursor = Cursors.ScrollWE;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        _downPt = pt;
        _downTime = XToTime(pt.X);
        var hit = HitTest(pt);
        if (hit == Drag.Playhead)
        {
            _drag = Drag.Playhead;
            SetPlayhead(_downTime);
            PlayheadDragged?.Invoke(Playhead);
            Cursor = Cursors.Hand;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && hit == Drag.None)
        {
            _drag = Drag.None;
            SetSelection(0, Duration);
            SelectionCommitted?.Invoke();
            return;
        }
        _drag = hit != Drag.None ? hit : Drag.Pending;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasData) return;
        var pt = e.GetPosition(this);
        double t = XToTime(pt.X);
        _hover = pt;

        switch (_drag)
        {
            case Drag.Pending:
                if (Math.Abs(pt.X - _downPt.X) > 3) { _drag = Drag.NewSel; goto case Drag.NewSel; }
                break;
            case Drag.NewSel:
                SetSelection(Math.Min(_downTime, t), Math.Max(_downTime, t));
                Cursor = Cursors.SizeWE;
                AutoScroll(pt.X);
                break;
            case Drag.Start:
                if (t > SelEnd) { _drag = Drag.End; SetSelection(SelEnd, t); }
                else SetSelection(t, SelEnd);
                AutoScroll(pt.X);
                break;
            case Drag.End:
                if (t < SelStart) { _drag = Drag.Start; SetSelection(t, SelStart); }
                else SetSelection(SelStart, t);
                AutoScroll(pt.X);
                break;
            case Drag.Playhead:
                SetPlayhead(t);
                PlayheadDragged?.Invoke(Playhead);
                AutoScroll(pt.X);
                break;
            case Drag.Pan:
                double dt = (pt.X - _downPt.X) / ActualWidth * (ViewEnd - ViewStart);
                SetView(_panViewStart - dt, _panViewStart - dt + (ViewEnd - ViewStart));
                break;
            default:
                Cursor = CursorFor(HitTest(pt));
                break;
        }
        InvalidateVisual();
    }

    private void AutoScroll(double x)
    {
        double span = ViewEnd - ViewStart;
        if (x < 0) SetView(ViewStart - span * 0.02, ViewEnd - span * 0.02);
        else if (x > ActualWidth) SetView(ViewStart + span * 0.02, ViewEnd + span * 0.02);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var d = _drag;
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        switch (d)
        {
            case Drag.Pending: SeekRequested?.Invoke(_downTime); break;
            case Drag.Playhead: SeekRequested?.Invoke(Playhead); break;
            case Drag.NewSel or Drag.Start or Drag.End: SelectionCommitted?.Invoke(); break;
        }
        Cursor = CursorFor(HitTest(e.GetPosition(this)));
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasData) return;
        double span = ViewEnd - ViewStart;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            double d = -Math.Sign(e.Delta) * span * 0.15;
            SetView(ViewStart + d, ViewEnd + d);
        }
        else
        {
            ZoomAt(XToTime(e.GetPosition(this).X), e.Delta > 0 ? 0.8 : 1.25);
        }
        e.Handled = true;
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }

    private static Pen MakeJoinPen()
    {
        var p = new Pen(Frozen(Color.FromArgb(0xC0, 0xFF, 0xA5, 0x3A)), 1) { DashStyle = new DashStyle([3, 3], 0) };
        p.Freeze();
        return p;
    }
}
