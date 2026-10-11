using System;
using System.Collections.Generic;
using System.Linq;
using HyPlayer.Services.Playback.Analysis;
using HyPlayer.Services.Playback.Analysis.Dsp;

namespace HyPlayer.Services.Playback.AutoMix;

public enum MixTechnique
{
    Crossfade,
    BeatMix,
    FilterCut,
    EchoOut,
    Gapless
}

public sealed class MixPlan
{
    public MixTechnique Technique { get; init; }
    public double AExit { get; init; }
    public double Overlap { get; init; }
    public double BEntry { get; init; }
    public double Rate { get; init; } = 1;
    public double RampBack { get; init; }
    public double BassSwapAt { get; init; } = -1;
    public double GainB { get; init; } = 1;
    public double BeatSeconds { get; init; }
    public int Bars { get; init; }
    public string Reason { get; init; } = string.Empty;
    public double AFadeEnd => AExit + Overlap;
    public double BContentAtOverlapEnd => BEntry + Overlap * Rate;
}

public sealed class AutoMixPlannerOptions
{
    public double MaxOverlapSeconds { get; init; } = 40;
    public int PreferredBars { get; init; } = 16;
    public double CrossfadeSeconds { get; init; } = 6;
    public bool AllowTimeStretch { get; init; } = true;
    public double MaxTempoDeviation { get; init; } = 0.06;
    public bool AlbumContinuation { get; init; }
}

public static class AutoMixPlanner
{
    public static MixPlan Plan(TrackAnalysis a, TrackAnalysis b,
        IReadOnlyList<(double Start, double End)> vocalsA,
        IReadOnlyList<(double Start, double End)> vocalsB,
        AutoMixPlannerOptions options)
    {
        var soundEndA = a.SoundEnd > 0 ? a.SoundEnd : a.Duration;
        var soundStartB = Math.Max(0, b.SoundStart);

        if (options.AlbumContinuation && a.Duration - soundEndA < 0.4 && soundStartB < 0.4)
            return new MixPlan { Technique = MixTechnique.Gapless, AExit = a.Duration, Overlap = 0, BEntry = 0, Reason = "album continuation" };

        var gainB = LoudnessMatch(a, b);
        var beatOk = a.HasBeatGrid && b.HasBeatGrid && a.BeatConfidence >= 0.55 && b.BeatConfidence >= 0.55 &&
                     a.Downbeats.Length >= 8 && b.Downbeats.Length >= 8;
        if (!beatOk) return Crossfade(a, b, soundEndA, soundStartB, gainB, options, "beat grid unreliable");

        var (rate, multiplier) = BestRate(a.Bpm, b.Bpm);
        var maxDeviation = options.AllowTimeStretch ? options.MaxTempoDeviation : 0.008;
        var technique = Math.Abs(rate - 1) <= maxDeviation ? MixTechnique.BeatMix : MixTechnique.FilterCut;
        if (technique == MixTechnique.FilterCut) rate = 1;
        if (!options.AllowTimeStretch) rate = 1;

        var beat = 60 / a.Bpm;
        var bar = 4 * beat;
        var outroBars = a.OutroStart > 0 ? (soundEndA - a.OutroStart) / bar : 8;
        var firstDownbeatB = b.Downbeats.FirstOrDefault(d => d >= soundStartB - 0.05);
        var barB = 4 * 60 / (b.Bpm * multiplier);
        var introBars = b.IntroEnd > firstDownbeatB ? (b.IntroEnd - firstDownbeatB) / barB : 8;
        var limit = Math.Min(options.PreferredBars, Math.Floor(options.MaxOverlapSeconds / bar));
        limit = Math.Min(limit, Math.Max(4, outroBars));
        limit = Math.Min(limit, Math.Max(4, introBars));
        var keyDistance = KeyEstimator.CamelotDistance(a.Key, b.Key);
        var keyClash = keyDistance > 2 && a.KeyConfidence > 0.03 && b.KeyConfidence > 0.03;
        if (keyClash || technique == MixTechnique.FilterCut) limit = Math.Min(limit, 8);
        var bars = SnapBars(limit);
        if (bars < 4 || bars * bar > options.MaxOverlapSeconds)
            return Crossfade(a, b, soundEndA, soundStartB, gainB, options, "overlap too short for beat mix");

        var reason = new List<string> { $"bpm {a.Bpm:0.0}->{b.Bpm:0.0} x{multiplier:0.##}", $"rate {rate:0.000}" };
        if (keyClash) reason.Add($"key distance {keyDistance}");
        while (true)
        {
            var overlap = bars * bar;
            var exit = ChooseExit(a, soundEndA, overlap);
            if (exit < 0) return Crossfade(a, b, soundEndA, soundStartB, gainB, options, "no downbeat exit");
            var bOverlap = overlap * rate;
            var entryB = ChooseEntry(b, vocalsB, firstDownbeatB, bOverlap, barB);
            var clash = VocalClash(vocalsA, exit, exit + overlap, vocalsB, entryB, entryB + bOverlap, rate);
            if (clash <= 1.5 || bars <= 4)
            {
                var useTechnique = technique;
                if (clash > 1.5)
                {
                    useTechnique = MixTechnique.EchoOut;
                    reason.Add($"vocal clash {clash:0.0}s");
                }
                if (entryB > firstDownbeatB + 0.05) reason.Add($"skip intro to {entryB:0.0}s");
                reason.Add($"{bars} bars");
                return new MixPlan
                {
                    Technique = useTechnique,
                    AExit = exit,
                    Overlap = useTechnique == MixTechnique.EchoOut ? Math.Min(overlap, 2 * bar) : overlap,
                    BEntry = entryB,
                    Rate = rate,
                    RampBack = Math.Abs(rate - 1) > 0.0005 ? Math.Min(16 * barB, 20) : 0,
                    BassSwapAt = useTechnique == MixTechnique.BeatMix ? 0.5 : -1,
                    GainB = gainB,
                    BeatSeconds = beat,
                    Bars = bars,
                    Reason = string.Join(", ", reason)
                };
            }
            bars /= 2;
        }
    }

    private static MixPlan Crossfade(TrackAnalysis a, TrackAnalysis b, double soundEndA, double soundStartB, double gainB, AutoMixPlannerOptions options, string reason)
    {
        var overlap = Math.Clamp(options.CrossfadeSeconds, 2, 12);
        overlap = Math.Min(overlap, Math.Max(1, soundEndA - 1));
        return new MixPlan
        {
            Technique = MixTechnique.Crossfade,
            AExit = Math.Max(0, soundEndA - overlap),
            Overlap = overlap,
            BEntry = soundStartB,
            Rate = 1,
            GainB = gainB,
            Reason = reason
        };
    }

    private static (double rate, double multiplier) BestRate(double bpmA, double bpmB)
    {
        var best = (rate: bpmA / bpmB, multiplier: 1.0);
        foreach (var m in new[] { 2.0, 0.5 })
        {
            var r = bpmA / (bpmB * m);
            if (Math.Abs(r - 1) < Math.Abs(best.rate - 1)) best = (r, m);
        }
        return best;
    }

    private static int SnapBars(double bars)
    {
        foreach (var candidate in new[] { 32, 16, 8, 4 })
            if (bars >= candidate) return candidate;
        return 0;
    }

    private static double ChooseExit(TrackAnalysis a, double soundEndA, double overlap)
    {
        var downbeats = a.Downbeats;
        var anchor = a.OutroStart > 0 ? Array.FindIndex(downbeats, d => d >= a.OutroStart - 0.05) : -1;
        var limit = soundEndA - overlap;
        var minimum = Math.Max(0, a.Duration - 45);
        var candidates = new List<int>();
        for (var i = downbeats.Length - 1; i >= 0; i--)
        {
            if (downbeats[i] > limit + 0.05) continue;
            if (downbeats[i] < minimum) break;
            candidates.Add(i);
        }
        if (candidates.Count == 0) return -1;
        var phase = anchor >= 0 ? anchor : 0;
        var phrase = candidates.FirstOrDefault(i => ((i - phase) % 4 + 4) % 4 == 0, -1);
        return downbeats[phrase >= 0 ? phrase : candidates[0]];
    }

    private static double ChooseEntry(TrackAnalysis b, IReadOnlyList<(double Start, double End)> vocalsB,
        double firstDownbeat, double bOverlap, double barB)
    {
        var firstVocal = double.MaxValue;
        foreach (var (start, end) in vocalsB)
            if (end - start >= 0.4 && start >= firstDownbeat - 0.5 && start < firstVocal) firstVocal = start;
        if (firstVocal == double.MaxValue) return firstDownbeat;
        if (firstVocal - firstDownbeat < bOverlap + 2 * barB) return firstDownbeat;
        var downbeats = b.Downbeats;
        var vocalBar = Array.FindLastIndex(downbeats, d => d <= firstVocal + barB * 0.25);
        if (vocalBar < 0) return firstDownbeat;
        var entryIndex = vocalBar - Math.Max(1, (int)Math.Round(bOverlap / barB));
        if (entryIndex < 0) return firstDownbeat;
        var entry = downbeats[entryIndex];
        if (entry <= firstDownbeat + barB || entry > b.Duration * 0.4) return firstDownbeat;
        return entry;
    }

    private static double VocalClash(IReadOnlyList<(double Start, double End)> vocalsA, double aStart, double aEnd,
        IReadOnlyList<(double Start, double End)> vocalsB, double bStart, double bEnd, double rate)
    {
        if (vocalsA.Count == 0 || vocalsB.Count == 0) return 0;
        double total = 0;
        foreach (var va in vocalsA)
        {
            var s1 = Math.Max(va.Start, aStart);
            var e1 = Math.Min(va.End, aEnd);
            if (e1 <= s1) continue;
            foreach (var vb in vocalsB)
            {
                var s2 = Math.Max(vb.Start, bStart);
                var e2 = Math.Min(vb.End, bEnd);
                if (e2 <= s2) continue;
                var outS2 = aStart + (s2 - bStart) / rate;
                var outE2 = aStart + (e2 - bStart) / rate;
                var overlap = Math.Min(e1, outE2) - Math.Max(s1, outS2);
                if (overlap > 0) total += overlap;
            }
        }
        return total;
    }

    private static double LoudnessMatch(TrackAnalysis a, TrackAnalysis b)
    {
        if (a.IntegratedLoudness <= -60 || b.IntegratedLoudness <= -60) return 1;
        var db = Math.Clamp(a.IntegratedLoudness - b.IntegratedLoudness, -6, 6);
        return Math.Pow(10, db / 20);
    }
}
