#nullable enable
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace HyPlayer.LyricRenderer.Builder;

public class CanvasImageBuilder(ICanvasImage source, List<IDisposable>? createdEffects = null)
{
    private ICanvasImage _source = source;

    private T Track<T>(T effect) where T : IDisposable
    {
        createdEffects?.Add(effect);
        return effect;
    }

    public CanvasImageBuilder AddOpacityEffect(float opacity)
    {
        _source = Track(new OpacityEffect
        {
            Source = _source,
            Opacity = opacity,
        });
        return this;
    }

    public CanvasImageBuilder AddTransform2DEffect(Matrix3x2 transformMatrix)
    {
        _source = Track(new Transform2DEffect
        {
            Source = _source,
            TransformMatrix = transformMatrix,
        });
        return this;
    }

    public CanvasImageBuilder AddGaussianBlurEffect(float blurAmount)
    {
        _source = Track(new GaussianBlurEffect
        {
            Source = _source,
            BlurAmount = blurAmount,
        });
        return this;
    }

    public CanvasImageBuilder AddShadowEffect(float blurAmount, Color color = default)
    {
        _source = Track(new ShadowEffect
        {
            Source = _source,
            BlurAmount = blurAmount,
            ShadowColor = color,
        });
        return this;
    }

    public CanvasImageBuilder AddCropEffect(Rect sourceRectangle, EffectBorderMode borderMode = EffectBorderMode.Soft)
    {
        _source = Track(new CropEffect
        {
            Source = _source,
            SourceRectangle = sourceRectangle,
            BorderMode = borderMode
        });
        return this;
    }

    public CanvasImageBuilder AddVignetteEffect(Color color, float amount, float curve = 1f)
    {
        _source = Track(new VignetteEffect
        {
            Source = _source,
            Amount = amount,
            Curve = curve,
            Color = color
        });
        return this;
    }
    public ICanvasImage Build()
    {
        return _source;
    }
}