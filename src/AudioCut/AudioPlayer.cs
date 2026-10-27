using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace AudioCut;

/// <summary>
/// Plays a list of source pieces back to back by streaming PCM from ffmpeg into WaveOut.
/// The next piece's ffmpeg is started ahead of time so joins are gapless.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private ChainProvider? _chain;
    private double _start;
    private int _session;

    public bool IsPlaying => _out != null;

    /// <summary>Volume multiplier applied live (0..3).</summary>
    public double Gain
    {
        get => _gain;
        set { _gain = value; if (_chain != null) _chain.Gain = (float)value; }
    }
    private double _gain = 1;

    /// <summary>Raised on the UI thread when playback reaches the end by itself.</summary>
    public event Action? Finished;

    /// <summary>Current position in timeline time.</summary>
    public double Position
    {
        get
        {
            var o = _out;
            if (o == null) return _start;
            try { return _start + o.GetPosition() / (double)(FFmpeg.PlaybackRate * FFmpeg.PlaybackChannels * 2); }
            catch { return _start; }
        }
    }

    /// <param name="timelineStart">Timeline time of the first piece (for <see cref="Position"/>).</param>
    public void Play(string path, IReadOnlyList<Piece> pieces, double timelineStart)
    {
        Stop();
        if (pieces.Count == 0) return;
        _start = timelineStart;
        int session = ++_session;

        _chain = new ChainProvider(path, pieces) { Gain = (float)_gain };
        _out = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
        var ui = SynchronizationContext.Current;
        _out.PlaybackStopped += (_, _) =>
        {
            void Done()
            {
                if (session != _session) return; // stale (we stopped / restarted)
                Cleanup();
                Finished?.Invoke();
            }
            if (ui != null) ui.Post(_ => Done(), null); else Done();
        };
        _out.Init(_chain);
        _out.Play();
    }

    public void Stop()
    {
        _session++;
        Cleanup();
    }

    private void Cleanup()
    {
        var o = _out; _out = null;
        var c = _chain; _chain = null;
        c?.Dispose();
        try { o?.Stop(); } catch { }
        o?.Dispose();
    }

    public void Dispose() => Stop();

    private sealed class ChainProvider : IWaveProvider, IDisposable
    {
        private readonly string _path;
        private readonly IReadOnlyList<Piece> _pieces;
        private readonly object _lock = new();
        private int _index;
        private Process? _current, _next;
        private bool _disposed;

        public WaveFormat WaveFormat { get; } = new(FFmpeg.PlaybackRate, 16, FFmpeg.PlaybackChannels);
        public volatile float Gain = 1;

        public ChainProvider(string path, IReadOnlyList<Piece> pieces)
        {
            _path = path;
            _pieces = pieces;
            _current = StartPiece(0);
            _next = StartPiece(1);
        }

        private Process? StartPiece(int i)
        {
            if (i >= _pieces.Count) return null;
            var p = FFmpeg.StartPlayback(_path, _pieces[i].SourceStart, _pieces[i].Length);
            _ = p.StandardError.ReadToEndAsync();
            return p;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            count -= count % WaveFormat.BlockAlign;
            int total = 0;
            while (total < count)
            {
                Process? cur;
                lock (_lock) { if (_disposed) break; cur = _current; }
                if (cur == null) break;
                int n;
                try { n = cur.StandardOutput.BaseStream.Read(buffer, offset + total, count - total); }
                catch { n = 0; }
                if (n > 0) { total += n; continue; }

                // piece finished: pad to a whole frame, move to the next one
                lock (_lock)
                {
                    if (_disposed) break;
                    cur.Dispose();
                    _index++;
                    _current = _next;
                    _next = StartPiece(_index + 1);
                }
            }
            total -= total % WaveFormat.BlockAlign;
            ApplyGain(buffer, offset, total);
            return total;
        }

        private void ApplyGain(byte[] buf, int offset, int count)
        {
            float g = Gain;
            if (Math.Abs(g - 1) < 0.001f) return;
            for (int i = offset; i < offset + count; i += 2)
            {
                int v = (int)((short)(buf[i] | (buf[i + 1] << 8)) * g);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                buf[i] = (byte)v;
                buf[i + 1] = (byte)(v >> 8);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                foreach (var p in new[] { _current, _next })
                {
                    try { p?.Kill(); } catch { }
                    p?.Dispose();
                }
                _current = _next = null;
            }
        }
    }
}
