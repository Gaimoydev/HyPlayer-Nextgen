#nullable enable
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace HyPlayer.LyricRenderer.LyricLineRenderers;

internal static class SyllableEmphasis
{
    private const long MinimumDuration = 1000;
    private const long FloatLead = 400;
    private const float FloatDurationScale = 1.4f;

    internal sealed class Group
    {
        public required long Start { get; init; }
        public required float Duration { get; init; }
        public required float Amount { get; init; }
        public required float Blur { get; init; }
        public required Rect[] Chars { get; init; }

        public long ActiveFrom => Start - FloatLead;
        public long ActiveUntil => Start + (long)(Duration / 2.5f + Duration * FloatDurationScale);
    }

    public static List<Group> Build(CanvasTextLayout layout, IReadOnlyList<RenderingSyllable> syllables, float renderStartX, float lift)
    {
        var groups = new List<Group>();
        if (syllables.Count == 0) return groups;

        var charRects = new List<Rect[]>(syllables.Count);
        var offset = 0;
        foreach (var syllable in syllables)
        {
            charRects.Add(MeasureCharacters(layout, syllable.Syllable, offset, renderStartX, lift));
            offset += syllable.Syllable.Length;
        }

        var lastWordIndex = syllables.Count - 1;
        while (lastWordIndex > 0 && string.IsNullOrWhiteSpace(syllables[lastWordIndex].Syllable)) lastWordIndex--;

        var index = 0;
        while (index < syllables.Count)
        {
            var first = index;
            var cjk = IsCjk(syllables[index].Syllable);
            if (!cjk)
            {
                while (index + 1 < syllables.Count && !IsCjk(syllables[index + 1].Syllable) &&
                       !EndsWithSpace(syllables[index].Syllable) && !StartsWithSpace(syllables[index + 1].Syllable))
                    index++;
            }
            var last = index;
            index++;

            var emphasize = false;
            var text = string.Empty;
            for (var i = first; i <= last; i++)
            {
                emphasize |= ShouldEmphasize(syllables[i].Syllable, syllables[i].Duration);
                text += syllables[i].Syllable;
            }
            var start = syllables[first].StartTime;
            var end = syllables[last].EndTime;
            if (!cjk) emphasize |= ShouldEmphasize(text, end - start);
            if (!emphasize) continue;

            var rects = new List<Rect>();
            for (var i = first; i <= last; i++) rects.AddRange(charRects[i]);
            if (rects.Count == 0) continue;

            var isLastWord = lastWordIndex >= first && lastWordIndex <= last;
            var du = (float)Math.Max(MinimumDuration, end - start);
            var amount = du / 2000f;
            amount = amount > 1 ? MathF.Sqrt(amount) : amount * amount * amount;
            var blur = du / 3000f;
            blur = blur > 1 ? MathF.Sqrt(blur) : blur * blur * blur;
            amount *= 0.6f;
            blur *= 0.5f;
            if (isLastWord)
            {
                amount *= 1.6f;
                blur *= 1.5f;
                du *= 1.2f;
            }

            groups.Add(new Group
            {
                Start = start,
                Duration = du,
                Amount = Math.Min(1.2f, amount),
                Blur = Math.Min(0.8f, blur),
                Chars = [.. rects]
            });
        }
        return groups;
    }

    public static bool AnyActive(List<Group> groups, long now)
    {
        foreach (var group in groups)
            if (now >= group.ActiveFrom && now <= group.ActiveUntil) return true;
        return false;
    }

    public static ICanvasImage Apply(ICanvasResourceCreator creator, ICanvasImage line, float textTop, long now, List<Group> groups,
        float fontSize, Color glowColor, Func<IDisposable, IDisposable> track)
    {
        if (groups.Count == 0 || !AnyActive(groups, now)) return line;

        var em = Math.Max(1f, fontSize);
        var padTop = 0.12f * em + 8;
        var padBottom = 0.06f * em + 4;

        var result = (CanvasCommandList)track(new CanvasCommandList(creator));
        using var ds = result.CreateDrawingSession();

        var bounds = line.GetBounds(creator);
        var keep = (CanvasGeometry)track(CanvasGeometry.CreateRectangle(creator,
            new Rect(bounds.X - 64, bounds.Y - 64, bounds.Width + 128, bounds.Height + 128)));
        var lifted = new List<(Rect Source, Matrix3x2 Transform, float GlowAlpha, float GlowRadius)>();

        foreach (var group in groups)
        {
            if (now < group.ActiveFrom || now > group.ActiveUntil) continue;
            var count = group.Chars.Length;
            for (var i = 0; i < count; i++)
            {
                var rect = group.Chars[i];
                if (rect.IsEmpty || rect.Width <= 0) continue;
                var charStart = group.Start + group.Duration / 2.5f / count * i;

                var glowT = Math.Clamp((now - charStart) / group.Duration, 0f, 1f);
                var trans = EmphasisEasing(glowT);
                var floatT = Math.Clamp((now - (charStart - FloatLead)) / (group.Duration * FloatDurationScale), 0f, 1f);
                var floatY = -MathF.Sin(floatT * MathF.PI) * 0.05f * em;

                var scale = 1 + trans * 0.1f * group.Amount;
                var offsetX = -trans * 0.03f * group.Amount * (count / 2f - i) * em;
                var offsetY = -trans * 0.025f * group.Amount * em + floatY;

                var source = new Rect(rect.X, rect.Y + textTop - padTop, rect.Width, rect.Height + padTop + padBottom);
                var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + textTop + rect.Height / 2));
                var transform = Matrix3x2.CreateScale(scale, center) * Matrix3x2.CreateTranslation(offsetX, offsetY);
                lifted.Add((source, transform, trans * group.Blur, Math.Min(0.3f, group.Blur * 0.3f) * em));

                var cut = (CanvasGeometry)track(CanvasGeometry.CreateRectangle(creator, source));
                keep = (CanvasGeometry)track(keep.CombineWith(cut, Matrix3x2.Identity, CanvasGeometryCombine.Exclude));
            }
        }

        using (ds.CreateLayer(1, keep))
        {
            ds.DrawImage(line);
        }

        foreach (var (source, transform, glowAlpha, glowRadius) in lifted)
        {
            var crop = (CropEffect)track(new CropEffect { Source = line, SourceRectangle = source });
            var moved = (Transform2DEffect)track(new Transform2DEffect { Source = crop, TransformMatrix = transform });
            if (glowAlpha > 0.01f && glowRadius > 0.1f)
            {
                var shadow = (ShadowEffect)track(new ShadowEffect
                {
                    Source = moved,
                    BlurAmount = glowRadius / 2,
                    ShadowColor = Color.FromArgb((byte)(Math.Clamp(glowAlpha, 0, 1) * 255), glowColor.R, glowColor.G, glowColor.B)
                });
                ds.DrawImage(shadow);
            }
            ds.DrawImage(moved);
        }

        return result;
    }

    private static Rect[] MeasureCharacters(CanvasTextLayout layout, string text, int baseIndex, float renderStartX, float lift)
    {
        var rects = new List<Rect>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (string.IsNullOrWhiteSpace(element)) continue;
            var regions = layout.GetCharacterRegions(baseIndex + enumerator.ElementIndex, element.Length);
            if (regions is not { Length: > 0 }) continue;
            var bounds = regions[0].LayoutBounds;
            for (var r = 1; r < regions.Length; r++) bounds.Union(regions[r].LayoutBounds);
            rects.Add(new Rect(bounds.X - renderStartX + SyllablesRenderingLyricLine.TextPadding, bounds.Y + lift, bounds.Width, bounds.Height));
        }
        return [.. rects];
    }

    private static bool ShouldEmphasize(string word, long duration)
    {
        if (IsCjk(word)) return duration >= MinimumDuration;
        var length = word.Trim().Length;
        return duration >= MinimumDuration && length is > 1 and <= 7;
    }

    private static bool IsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or
                >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= 'ᄀ' and <= 'ᇿ')
                return true;
        }
        return false;
    }

    private static bool EndsWithSpace(string text) => text.Length > 0 && char.IsWhiteSpace(text[^1]);
    private static bool StartsWithSpace(string text) => text.Length > 0 && char.IsWhiteSpace(text[0]);

    private static float EmphasisEasing(float x)
    {
        const float mid = 0.5f;
        return x < mid ? CubicBezier(0.2f, 0.4f, 0.58f, 1f, x / mid) : 1 - CubicBezier(0.3f, 0f, 0.58f, 1f, (x - mid) / (1 - mid));
    }

    private static float CubicBezier(float x1, float y1, float x2, float y2, float x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var current = Sample(x1, x2, t) - x;
            var slope = SampleDerivative(x1, x2, t);
            if (MathF.Abs(current) < 1e-5f) break;
            if (MathF.Abs(slope) < 1e-6f) break;
            t = Math.Clamp(t - current / slope, 0, 1);
        }
        return Sample(y1, y2, t);
    }

    private static float Sample(float p1, float p2, float t)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static float SampleDerivative(float p1, float p2, float t)
    {
        var u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }
}
