using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AudioCut;

public partial class MainWindow : Window
{
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private readonly Settings _settings = Settings.Load();
    private CancellationTokenSource? _loadCts;
    private MediaInfo? _media;
    private ExportFormat _format;
    private double _playEnd;
    private bool _saving;
    private string? _lastSaved;
    private DispatcherTimer? _toastTimer;
    private double _gain = 1;

    // undo history: every finished edit (selection, trim, delete) is a snapshot
    private sealed record EditState(Timeline Timeline, double SelStart, double SelEnd);
    private readonly List<EditState> _history = [];
    private int _historyIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        _format = ExportFormat.All.FirstOrDefault(f => f.Key == _settings.Format) ?? ExportFormat.All[0];
        FadeCheck.IsChecked = _settings.Fade;
        BuildFormatPicker();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => UpdatePlayback();

        Wave.SelectionChanged += OnSelectionChanged;
        Wave.ViewChanged += SyncScrollBar;
        Wave.SeekRequested += Seek;
        Wave.SelectionCommitted += Commit;
        Wave.PlayheadDragged += UpdatePositionText;
        _player.Finished += OnPlaybackFinished;

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DragOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { _player.Dispose(); _loadCts?.Cancel(); };

        Loaded += async (_, _) =>
        {
            try { await Task.Run(FFmpeg.EnsureAvailable); }
            catch (Exception ex) { MessageBox.Show(this, "Nie udało się przygotować ffmpeg:\n" + ex.Message, "AudioCut"); }
            var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (arg != null) await OpenFile(arg);
        };
    }

    // ---------------- format picker ----------------

    private void BuildFormatPicker()
    {
        foreach (var f in ExportFormat.All)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                Content = f.Label,
                GroupName = "fmt",
                IsChecked = f == _format,
                ToolTip = f.Key switch
                {
                    "mp3-320" => "MP3, najlepsza jakość",
                    "mp3-192" => "MP3, dobra jakość, mniejszy plik",
                    "mp3-128" => "MP3, najmniejszy plik",
                    "m4a" => "AAC 256 kbps – dobre dla iPhone / Apple",
                    "wav" => "Bez kompresji – największy plik",
                    "flac" => "Bezstratna kompresja",
                    _ => null
                }
            };
            rb.Checked += (_, _) => { _format = f; _settings.Format = f.Key; _settings.Save(); };
            FormatPanel.Children.Add(rb);
        }
        FadeCheck.Checked += (_, _) => { _settings.Fade = true; _settings.Save(); };
        FadeCheck.Unchecked += (_, _) => { _settings.Fade = false; _settings.Save(); };
    }

    // ---------------- opening files ----------------

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Wybierz plik audio lub wideo",
            Filter = "Pliki audio i wideo|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.3gp;" +
                     "*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.amr|Wszystkie pliki|*.*",
            InitialDirectory = Directory.Exists(_settings.LastOpenDir) ? _settings.LastOpenDir : null,
        };
        if (dlg.ShowDialog(this) == true) await OpenFile(dlg.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && !_saving;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DragOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (_saving) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && File.Exists(files[0]))
            await OpenFile(files[0]);
    }

    private async Task OpenFile(string path)
    {
        _player.Stop();
        SetPlayIcon(false);
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _settings.LastOpenDir = Path.GetDirectoryName(path);
        _settings.Save();

        HideToast();
        ShowState(loading: true);
        LoadingText.Text = "Otwieram " + Path.GetFileName(path) + "…";

        MediaInfo info;
        try
        {
            info = await FFmpeg.ProbeAsync(path);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ShowState(loading: false);
            MessageBox.Show(this, ex.Message, "Nie można otworzyć pliku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (cts.IsCancellationRequested) return;

        _media = info;
        FileTitle.Text = Path.GetFileName(path);
        Wave.Reset(info.Duration);
        VolumeSlider.Value = 100;
        _history.Clear();
        _historyIndex = -1;
        Commit();
        UpdateTimelineInfo();
        ShowState(loading: false);
        Wave.Focus();

        Wave.IsLoadingPeaks = true;
        var chunk = new float[2 * 4000];
        int pairs = 0;
        var flushWatch = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => FFmpeg.ComputePeaksAsync(path, WaveformView.PeaksPerSecond, (mn, mx) =>
            {
                chunk[pairs * 2] = mn;
                chunk[pairs * 2 + 1] = mx;
                if (++pairs == chunk.Length / 2 || flushWatch.ElapsedMilliseconds > 120)
                {
                    var copy = (float[])chunk.Clone();
                    int n = pairs;
                    pairs = 0;
                    flushWatch.Restart();
                    Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) Wave.AddPeaks(copy, n); });
                }
            }, cts.Token), cts.Token);
            if (pairs > 0)
            {
                var n = pairs;
                await Dispatcher.InvokeAsync(() => { if (!cts.IsCancellationRequested) Wave.AddPeaks(chunk, n); });
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { /* waveform is optional - cutting still works */ }
        if (cts.IsCancellationRequested) return;
        Wave.IsLoadingPeaks = false;
        Wave.FinishPeaks();
        if (_media.Duration <= 0)
        {
            _media = _media with { Duration = Wave.Duration };
            _history.Clear();
            _historyIndex = -1;
            Commit();
            UpdateTimelineInfo();
        }
        UpdateClipWarning();
    }

    private void UpdateTimelineInfo()
    {
        if (_media == null) return;
        var t = Wave.Timeline;
        string kind = _media.HasVideo ? "Wideo" : "Audio";
        int cuts = t.Segments.Count - 1;
        string edit = t.IsOriginal ? TimeFormat.Full(t.Duration)
            : $"po edycji {TimeFormat.Full(t.Duration)} (oryginał {TimeFormat.Full(_media.Duration)})" +
              (cuts > 0 ? $"  ·  sklejeń: {cuts}" : "");
        FileInfo.Text = $"{kind}  ·  {edit}  ·  {Path.GetDirectoryName(_media.Path)}";
        DurationText.Text = "/ " + TimeFormat.Full(t.Duration);
    }

    // ---------------- undo / edits ----------------

    private EditState Snapshot() => new(Wave.Timeline, Wave.SelStart, Wave.SelEnd);

    /// <summary>Records the current state as an undo step (no-op if nothing changed).</summary>
    private void Commit()
    {
        var now = Snapshot();
        if (_historyIndex >= 0)
        {
            var top = _history[_historyIndex];
            if (top.Timeline == now.Timeline && Math.Abs(top.SelStart - now.SelStart) < 1e-6 && Math.Abs(top.SelEnd - now.SelEnd) < 1e-6)
                return;
        }
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(now);
        if (_history.Count > 300) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        UpdateUndoButtons();
    }

    private void Restore(EditState st)
    {
        bool timelineChanged = st.Timeline != Wave.Timeline;
        if (timelineChanged) StopPlayback();
        if (timelineChanged) Wave.SetTimeline(st.Timeline, st.SelStart, st.SelEnd);
        else Wave.SetSelection(st.SelStart, st.SelEnd);
        UpdatePositionText(Wave.Playhead);
        UpdateTimelineInfo();
        UpdateUndoButtons();
    }

    private void Undo()
    {
        if (_historyIndex <= 0) return;
        Restore(_history[--_historyIndex]);
    }

    private void Redo()
    {
        if (_historyIndex >= _history.Count - 1) return;
        Restore(_history[++_historyIndex]);
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _historyIndex > 0;
        RedoButton.IsEnabled = _historyIndex < _history.Count - 1;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Trim_Click(object sender, RoutedEventArgs e) => Trim();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private bool IsWholeSelected => Wave.SelStart <= 0.001 && Wave.SelEnd >= Wave.Duration - 0.001;

    /// <summary>Keep only the selection.</summary>
    private void Trim()
    {
        if (!HasSelection || IsWholeSelected) return;
        StopPlayback();
        double ph = Math.Clamp(Wave.Playhead - Wave.SelStart, 0, Wave.SelEnd - Wave.SelStart);
        var t = Wave.Timeline.Keep(Wave.SelStart, Wave.SelEnd);
        Wave.SetTimeline(t, 0, t.Duration);
        Wave.SetView(0, t.Duration);
        Wave.SetPlayhead(ph);
        UpdatePositionText(ph);
        Commit();
        UpdateTimelineInfo();
    }

    /// <summary>Remove the selection and join what is left.</summary>
    private void DeleteSelection()
    {
        if (!HasSelection || IsWholeSelected) return;
        StopPlayback();
        double cut = Wave.SelStart;
        var t = Wave.Timeline.Remove(Wave.SelStart, Wave.SelEnd);
        Wave.SetTimeline(t, 0, t.Duration);
        Wave.SetPlayhead(Math.Min(cut, t.Duration));
        UpdatePositionText(Wave.Playhead);
        Commit();
        UpdateTimelineInfo();
    }

    // ---------------- volume ----------------

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return; // during InitializeComponent
        _gain = e.NewValue / 100.0;
        _player.Gain = _gain;
        Wave.Gain = _gain;
        VolumeText.Text = $"{e.NewValue:0}%";
        VolumeIcon.Text = e.NewValue <= 0 ? "\uE74F" : e.NewValue < 50 ? "\uE993" : e.NewValue < 120 ? "\uE994" : "\uE995";
        UpdateClipWarning();
    }

    private void VolumeReset_Click(object sender, RoutedEventArgs e) => VolumeSlider.Value = 100;

    private void UpdateClipWarning() =>
        ClipWarn.Text = Wave.PeakMax * _gain > 1.0 ? "  · może przesterować" : "";

    private void ShowState(bool loading)
    {
        bool has = _media != null && !loading;
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        DropZone.Visibility = !loading && _media == null ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = Editor.Visibility;
        OpenButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (loading) Editor.Visibility = Visibility.Collapsed;
    }

    // ---------------- selection / view ----------------

    private void OnSelectionChanged()
    {
        if (!StartBox.IsKeyboardFocused) StartBox.Text = TimeFormat.Full(Wave.SelStart);
        if (!EndBox.IsKeyboardFocused) EndBox.Text = TimeFormat.Full(Wave.SelEnd);
        double len = Wave.SelEnd - Wave.SelStart;
        LengthText.Text = TimeFormat.Length(len);
        SaveButton.IsEnabled = len >= 0.05 && !_saving;
        TrimButton.IsEnabled = DeleteButton.IsEnabled = len >= 0.05 && !IsWholeSelected;
        if (_player.IsPlaying) _playEnd = PlaybackEnd(_player.Position);
    }

    private void SyncScrollBar()
    {
        double span = Wave.ViewEnd - Wave.ViewStart;
        ViewScroll.Minimum = 0;
        ViewScroll.Maximum = Math.Max(0, Wave.Duration - span);
        ViewScroll.ViewportSize = span;
        ViewScroll.LargeChange = span * 0.9;
        ViewScroll.SmallChange = span * 0.1;
        ViewScroll.Value = Wave.ViewStart;
        ViewScroll.Visibility = span < Wave.Duration - 0.001 ? Visibility.Visible : Visibility.Hidden;
    }

    private void ViewScroll_Scroll(object sender, ScrollEventArgs e)
    {
        double span = Wave.ViewEnd - Wave.ViewStart;
        Wave.SetView(e.NewValue, e.NewValue + span);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Wave.ZoomAt(ZoomCenter(), 0.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Wave.ZoomAt(ZoomCenter(), 2);
    private void ZoomSel_Click(object sender, RoutedEventArgs e) => Wave.ZoomToSelection();
    private void ZoomAll_Click(object sender, RoutedEventArgs e) => Wave.SetView(0, Wave.Duration);

    private double ZoomCenter() =>
        Wave.Playhead >= Wave.ViewStart && Wave.Playhead <= Wave.ViewEnd ? Wave.Playhead : (Wave.ViewStart + Wave.ViewEnd) / 2;

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyTimeBox((TextBox)sender); Wave.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { OnSelectionChangedTextsOnly(); Wave.Focus(); e.Handled = true; }
    }

    private void TimeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyTimeBox((TextBox)sender);

    private void ApplyTimeBox(TextBox box)
    {
        if (_media == null) return;
        if (TimeFormat.TryParse(box.Text, out var t))
        {
            t = Math.Clamp(t, 0, Wave.Duration);
            if (box == StartBox) Wave.SetSelection(t, Math.Max(t, Wave.SelEnd));
            else Wave.SetSelection(Math.Min(t, Wave.SelStart), t);
            Wave.EnsureVisible(box == StartBox ? Wave.SelStart : Wave.SelEnd);
            Commit();
        }
        OnSelectionChangedTextsOnly();
    }

    private void OnSelectionChangedTextsOnly()
    {
        StartBox.Text = TimeFormat.Full(Wave.SelStart);
        EndBox.Text = TimeFormat.Full(Wave.SelEnd);
    }

    private void SetStart_Click(object sender, RoutedEventArgs e) => SetEdge(start: true);
    private void SetEnd_Click(object sender, RoutedEventArgs e) => SetEdge(start: false);

    private void SetEdge(bool start)
    {
        if (_media == null) return;
        double t = CurrentTime();
        if (start) Wave.SetSelection(t, t > Wave.SelEnd ? Wave.Duration : Wave.SelEnd);
        else Wave.SetSelection(t < Wave.SelStart ? 0 : Wave.SelStart, t);
        Commit();
    }

    // ---------------- playback ----------------

    private double CurrentTime() => _player.IsPlaying ? _player.Position : Wave.Playhead;

    private bool HasSelection => Wave.SelEnd - Wave.SelStart >= 0.05;

    /// <summary>Playback stops at the selection end if we're inside the selection, otherwise at the file end.</summary>
    private double PlaybackEnd(double from) =>
        HasSelection && from < Wave.SelEnd - 0.01 && from >= Wave.SelStart - 0.01 ? Wave.SelEnd : Wave.Duration;

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_media == null) return;
        if (_player.IsPlaying)
        {
            Wave.SetPlayhead(_player.Position);
            StopPlayback();
            return;
        }
        double from = Wave.Playhead;
        if (HasSelection && (from < Wave.SelStart || from >= Wave.SelEnd - 0.02)) from = Wave.SelStart;
        if (!HasSelection && from >= Wave.Duration - 0.05) from = 0;
        StartPlayback(from);
    }

    private void StartPlayback(double from)
    {
        if (_media == null) return;
        _playEnd = PlaybackEnd(from);
        Wave.SetPlayhead(from);
        _player.Play(_media.Path, Wave.Timeline.Pieces(from, _playEnd), from);
        SetPlayIcon(true);
        _timer.Start();
    }

    private void StopPlayback()
    {
        if (!_player.IsPlaying) return;
        _player.Stop();
        _timer.Stop();
        SetPlayIcon(false);
    }

    private void ToSelStart_Click(object sender, RoutedEventArgs e) => GoToSelectionStart();

    private void GoToSelectionStart()
    {
        if (_media == null) return;
        Seek(Wave.SelStart);
        Wave.EnsureVisible(Wave.SelStart);
    }

    private void Seek(double t)
    {
        if (_player.IsPlaying) StartPlayback(t);
        else { Wave.SetPlayhead(t); UpdatePositionText(t); }
    }

    private void UpdatePlayback()
    {
        if (!_player.IsPlaying) { _timer.Stop(); return; }
        double pos = _player.Position;
        if (pos >= _playEnd)
        {
            OnPlaybackFinished();
            return;
        }
        if (Wave.IsDraggingPlayhead) return;
        // Follow the playhead only when it runs off the right edge of a view it was visible in.
        // If the user zoomed/scrolled elsewhere (e.g. "Do zaznaczenia"), leave the view alone.
        double prev = Wave.Playhead;
        bool wasVisible = prev >= Wave.ViewStart && prev <= Wave.ViewEnd;
        Wave.SetPlayhead(pos);
        if (wasVisible && pos > Wave.ViewEnd)
        {
            double span = Wave.ViewEnd - Wave.ViewStart;
            Wave.SetView(pos - span * 0.05, pos + span * 0.95);
        }
        UpdatePositionText(pos);
    }

    private void OnPlaybackFinished()
    {
        bool wasSel = HasSelection && Math.Abs(_playEnd - Wave.SelEnd) < 0.001;
        _player.Stop();
        _timer.Stop();
        SetPlayIcon(false);
        double back = wasSel ? Wave.SelStart : _playEnd;
        Wave.SetPlayhead(back);
        UpdatePositionText(back);
    }

    private void UpdatePositionText(double t) => PositionText.Text = TimeFormat.Full(t);

    private void SetPlayIcon(bool playing)
    {
        PlayIcon.Text = playing ? "" : "";
        PlayIcon.Margin = playing ? new Thickness(0) : new Thickness(2, 0, 0, 0);
    }

    // ---------------- keyboard ----------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { Open_Click(this, e); e.Handled = true; return; }
        if (_media == null || Editor.Visibility != Visibility.Visible) return;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { if (SaveButton.IsEnabled) Save_Click(this, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; return; }
        if ((e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) ||
            (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))) { Redo(); e.Handled = true; return; }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.I: SetEdge(true); break;
            case Key.O: SetEdge(false); break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.None: Wave.ZoomToSelection(); break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.None: Wave.SetView(0, Wave.Duration); break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control: Wave.SetSelection(0, Wave.Duration); Commit(); break;
            case Key.T: Trim(); break;
            case Key.Delete or Key.Back: DeleteSelection(); break;
            case Key.Left: Seek(Math.Max(0, CurrentTime() - (shift ? 5 : 1))); Wave.EnsureVisible(Wave.Playhead); break;
            case Key.Right: Seek(Math.Min(Wave.Duration, CurrentTime() + (shift ? 5 : 1))); Wave.EnsureVisible(Wave.Playhead); break;
            case Key.Home: GoToSelectionStart(); break;
            case Key.End: Seek(HasSelection ? Wave.SelEnd : Wave.Duration); Wave.EnsureVisible(Wave.Playhead); break;
            case Key.OemPlus or Key.Add: Wave.ZoomAt(ZoomCenter(), 0.5); break;
            case Key.OemMinus or Key.Subtract: Wave.ZoomAt(ZoomCenter(), 2); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------- export ----------------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null || _saving) return;
        double start = Wave.SelStart, end = Wave.SelEnd;
        if (end - start < 0.05) return;

        var srcDir = Path.GetDirectoryName(_media.Path)!;
        var baseName = Path.GetFileNameWithoutExtension(_media.Path);
        bool whole = start < 0.01 && end > Wave.Duration - 0.01;
        var pieces = Wave.Timeline.Pieces(start, end);
        var suggested = whole && Wave.Timeline.IsOriginal ? baseName
            : pieces.Count == 1 ? $"{baseName} ({TimeFormat.FileSafe(pieces[0].SourceStart)} - {TimeFormat.FileSafe(pieces[0].SourceEnd)})"
            : $"{baseName} (edytowany)";

        var dlg = new SaveFileDialog
        {
            Title = "Zapisz fragment jako",
            FileName = suggested + "." + _format.Extension,
            DefaultExt = _format.Extension,
            Filter = $"{_format.Label} (*.{_format.Extension})|*.{_format.Extension}",
            InitialDirectory = Directory.Exists(_settings.LastSaveDir) ? _settings.LastSaveDir : srcDir,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        var output = dlg.FileName;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_media.Path), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Nie można nadpisać pliku źródłowego. Wybierz inną nazwę.", "AudioCut", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.LastSaveDir = Path.GetDirectoryName(output);
        _settings.Save();

        _saving = true;
        HideToast();
        SaveButton.IsEnabled = false;
        SaveLabel.Visibility = Visibility.Collapsed;
        SavingLabel.Visibility = Visibility.Visible;
        SaveProgress.Value = 0;
        SavingText.Text = "Zapisuję… 0%";
        var progress = new Progress<double>(p =>
        {
            SaveProgress.Value = p;
            SavingText.Text = $"Zapisuję… {p * 100:0}%";
        });
        try
        {
            await FFmpeg.ExportAsync(_media.Path, output, pieces, _format, FadeCheck.IsChecked == true, _gain, progress, CancellationToken.None);
            _lastSaved = output;
            ShowToast(Path.GetFileName(output));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Nie udało się zapisać", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saving = false;
            SaveLabel.Visibility = Visibility.Visible;
            SavingLabel.Visibility = Visibility.Collapsed;
            OnSelectionChanged();
        }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (_, _) => HideToast();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        if (Toast.Visibility != Visibility.Visible) return;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, a);
    }

    private void CloseToast_Click(object sender, RoutedEventArgs e) => HideToast();

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSaved != null && File.Exists(_lastSaved))
            Process.Start("explorer.exe", $"/select,\"{_lastSaved}\"");
        HideToast();
    }

    private void PlaySaved_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSaved != null && File.Exists(_lastSaved))
            Process.Start(new ProcessStartInfo(_lastSaved) { UseShellExecute = true });
    }
}
