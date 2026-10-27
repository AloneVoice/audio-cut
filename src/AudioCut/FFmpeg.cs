using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioCut;

public sealed record MediaInfo(string Path, double Duration, bool HasVideo, string AudioDescription);

public sealed record ExportFormat(string Key, string Label, string Extension, string[] CodecArgs)
{
    public static readonly ExportFormat[] All =
    [
        new("mp3-320", "MP3 320", "mp3", ["-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3"]),
        new("mp3-192", "MP3 192", "mp3", ["-c:a", "libmp3lame", "-b:a", "192k", "-id3v2_version", "3"]),
        new("mp3-128", "MP3 128", "mp3", ["-c:a", "libmp3lame", "-b:a", "128k", "-id3v2_version", "3"]),
        new("m4a", "M4A", "m4a", ["-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart"]),
        new("wav", "WAV", "wav", ["-c:a", "pcm_s16le"]),
        new("flac", "FLAC", "flac", ["-c:a", "flac"]),
    ];
}

public static class FFmpeg
{
    public const int PlaybackRate = 44100;
    public const int PlaybackChannels = 2;

    private static string? _exe;

    public static string Exe => _exe ?? throw new InvalidOperationException("ffmpeg not initialised");

    /// <summary>Finds ffmpeg next to the exe, or unpacks the embedded copy into %LOCALAPPDATA%.</summary>
    public static void EnsureAvailable()
    {
        if (_exe != null) return;

        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) { _exe = local; return; }

        var asm = Assembly.GetExecutingAssembly();
        using var res = asm.GetManifestResourceStream("ffmpeg.exe.gz");
        if (res == null)
        {
            _exe = "ffmpeg"; // fall back to PATH (dev builds)
            return;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioCut");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"ffmpeg-{res.Length}.exe");
        if (!File.Exists(target))
        {
            var tmp = target + ".tmp";
            using (var gz = new GZipStream(res, CompressionMode.Decompress))
            using (var fs = File.Create(tmp))
                gz.CopyTo(fs);
            File.Move(tmp, target, overwrite: true);
            foreach (var old in Directory.GetFiles(dir, "ffmpeg-*.exe"))
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { /* in use - ignore */ }
        }
        _exe = target;
    }

    public static Process Start(IEnumerable<string> args, bool redirectStdout = true)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("Nie udało się uruchomić ffmpeg.");
    }

    public static async Task<MediaInfo> ProbeAsync(string path)
    {
        using var p = Start(["-hide_banner", "-i", path], redirectStdout: false);
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        var audio = Regex.Match(err, @"Stream #\S+.*?: Audio: (.+)");
        if (!audio.Success)
        {
            if (err.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) || err.Contains("No such file"))
                throw new InvalidDataException("Nie rozpoznano formatu pliku.");
            throw new InvalidDataException("Ten plik nie zawiera ścieżki audio.");
        }

        double duration = 0;
        var d = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (d.Success)
            duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 +
                       double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);

        var hasVideo = Regex.IsMatch(err, @"Stream #\S+.*?: Video: (?!mjpeg|png)");
        var desc = audio.Groups[1].Value.Trim();
        return new MediaInfo(path, duration, hasVideo, desc);
    }

    /// <summary>
    /// Decodes the audio track at a low rate and reports min/max peaks
    /// (<paramref name="peaksPerSecond"/> per second) as they become available.
    /// </summary>
    public static async Task ComputePeaksAsync(string path, int peaksPerSecond, Action<float, float> onPeak, CancellationToken ct)
    {
        const int rate = 8000;
        int perPeak = rate / peaksPerSecond;
        using var p = Start(["-hide_banner", "-nostdin", "-i", path, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", rate.ToString(),
                             "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1"]);
        _ = p.StandardError.ReadToEndAsync(ct);
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });

        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[perPeak * 2 * 256];
        int carry = 0, n = 0;
        float min = 0, max = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buf.AsMemory(carry, buf.Length - carry), ct);
            if (read == 0) break;
            int total = carry + read;
            int samples = total / 2;
            for (int i = 0; i < samples; i++)
            {
                float s = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8)) / 32768f;
                if (s < min) min = s;
                if (s > max) max = s;
                if (++n == perPeak)
                {
                    onPeak(min, max);
                    n = 0; min = 0; max = 0;
                }
            }
            carry = total % 2;
            if (carry == 1) buf[0] = buf[total - 1];
        }
        if (n > 0) onPeak(min, max);
        await p.WaitForExitAsync(ct);
    }

    public static Process StartPlayback(string path, double start, double length) =>
        Start(["-hide_banner", "-nostdin", "-ss", Fmt(start), "-i", path, "-t", Fmt(length), "-map", "0:a:0", "-vn",
               "-ac", PlaybackChannels.ToString(), "-ar", PlaybackRate.ToString(), "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1"]);

    public static async Task ExportAsync(string input, string output, IReadOnlyList<Piece> pieces, ExportFormat format,
                                         bool fade, double gain, IProgress<double> progress, CancellationToken ct)
    {
        double len = pieces.Sum(x => x.Length);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        foreach (var piece in pieces)
            args.AddRange(["-ss", Fmt(piece.SourceStart), "-t", Fmt(piece.Length), "-i", input]);

        var filters = new List<string>();
        if (Math.Abs(gain - 1) > 0.001)
        {
            filters.Add($"volume={Fmt(gain)}");
            if (gain > 1) filters.Add("alimiter=limit=0.98:level=disabled");
        }
        if (fade)
        {
            double f = Math.Min(1.0, len / 4);
            filters.Add($"afade=t=in:st=0:d={Fmt(f)}");
            filters.Add($"afade=t=out:st={Fmt(len - f)}:d={Fmt(f)}");
        }

        if (pieces.Count == 1)
        {
            args.AddRange(["-map", "0:a:0"]);
            if (filters.Count > 0) args.AddRange(["-af", string.Join(",", filters)]);
        }
        else
        {
            // resample every piece to a common format so concat never complains
            var graph = new System.Text.StringBuilder();
            for (int i = 0; i < pieces.Count; i++)
                graph.Append($"[{i}:a:0]aresample=async=1,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[p{i}];");
            for (int i = 0; i < pieces.Count; i++) graph.Append($"[p{i}]");
            graph.Append($"concat=n={pieces.Count}:v=0:a=1");
            if (filters.Count > 0) graph.Append(',').Append(string.Join(",", filters));
            graph.Append("[out]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[out]"]);
        }
        args.AddRange(["-vn", "-sn", "-dn"]);
        args.AddRange(format.CodecArgs);
        args.AddRange(["-progress", "pipe:1", "-nostats", output]);

        using var p = Start(args);
        var errTask = p.StandardError.ReadToEndAsync();
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });

        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) && len > 0)
                progress.Report(Math.Clamp(us / 1e6 / len, 0, 1));
        }
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            var err = await errTask;
            var lastLines = string.Join("\n", err.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
            throw new IOException("ffmpeg zakończył się błędem:\n" + lastLines);
        }
        progress.Report(1);
    }

    private static string Fmt(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
