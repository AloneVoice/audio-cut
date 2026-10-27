namespace AudioCut;

public readonly record struct Segment(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>A piece of the source that maps onto the edited timeline starting at <see cref="At"/>.</summary>
public readonly record struct Piece(double SourceStart, double SourceEnd, double At)
{
    public double Length => SourceEnd - SourceStart;
}

/// <summary>
/// Immutable, non-destructive edit: the list of source ranges that are kept, played back to back.
/// All public positions ("timeline time") refer to the edited result.
/// </summary>
public sealed class Timeline
{
    private const double Eps = 0.0005;

    public IReadOnlyList<Segment> Segments { get; }
    public double Duration { get; }
    public bool IsOriginal { get; }

    private Timeline(IReadOnlyList<Segment> segments, bool original)
    {
        Segments = segments;
        Duration = segments.Sum(s => s.Length);
        IsOriginal = original;
    }

    public static Timeline Full(double duration) => new([new Segment(0, Math.Max(0, duration))], true);

    /// <summary>Source pieces covering timeline range [from, to].</summary>
    public List<Piece> Pieces(double from, double to)
    {
        var list = new List<Piece>();
        double at = 0;
        foreach (var s in Segments)
        {
            double a = Math.Max(from, at), b = Math.Min(to, at + s.Length);
            if (b - a > Eps)
                list.Add(new Piece(s.Start + (a - at), s.Start + (b - at), a));
            at += s.Length;
            if (at >= to) break;
        }
        return list;
    }

    public double ToSource(double t)
    {
        double at = 0;
        foreach (var s in Segments)
        {
            if (t < at + s.Length) return s.Start + Math.Max(0, t - at);
            at += s.Length;
        }
        return Segments.Count > 0 ? Segments[^1].End : 0;
    }

    /// <summary>Timeline positions where two non-adjacent source pieces meet.</summary>
    public IEnumerable<double> Joins()
    {
        double at = 0;
        for (int i = 0; i < Segments.Count - 1; i++)
        {
            at += Segments[i].Length;
            yield return at;
        }
    }

    public Timeline Keep(double from, double to) => Build(Pieces(from, to));

    public Timeline Remove(double from, double to) => Build([.. Pieces(0, from), .. Pieces(to, Duration)]);

    private Timeline Build(IEnumerable<Piece> pieces)
    {
        var segs = new List<Segment>();
        foreach (var p in pieces)
        {
            if (segs.Count > 0 && Math.Abs(segs[^1].End - p.SourceStart) < Eps)
                segs[^1] = segs[^1] with { End = p.SourceEnd };
            else
                segs.Add(new Segment(p.SourceStart, p.SourceEnd));
        }
        return new Timeline(segs, false);
    }
}
