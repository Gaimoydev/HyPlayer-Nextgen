using System;
using System.Threading.Tasks;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using HyPlayer.UWP.Chopin.Abstractions.Interfaces;
using HyPlayer.UWP.Chopin.Abstractions.Models;

namespace HyPlayer.Services.Playback.Transitions;

public sealed partial class FadeTransition : ITrackTransition, IDisposable
{
    private readonly Setting _setting;
    private readonly IPlayer _player;
    private readonly NextTrackPreloader _preloader = new();
    private AudioGraphPlaybackSource? _fadingSource;
    private HyPlayItem? _fadingItem;
    private bool _disposed;

    public string Id => "fde";

    public FadeTransition(Setting setting, IPlayer player)
    {
        _setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    private TimeSpan FadeOutTime => TimeSpan.FromSeconds(Math.Clamp(_setting.CrossFadeTime, 1, 12));
    private TimeSpan FadeInTime => TimeSpan.FromSeconds(Math.Clamp(_setting.CrossFadeTime / 2, 0.5, 4));

    public void OnPositionTick(TrackTransitionContext ctx)
    {
        if (_preloader.IsReady && !ReferenceEquals(_preloader.ForItem, ctx.CurrentItem))
            _preloader.Discard(ctx.Player);
        _preloader.TryPreload(ctx, FadeOutTime + TimeSpan.FromSeconds(15));

        if (_fadingSource is not null) return;
        var remaining = ctx.Duration - ctx.Position;
        if (remaining > FadeOutTime || remaining < TimeSpan.FromMilliseconds(200)) return;
        if (ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is not AudioGraphPlaybackSource current) return;
        _fadingSource = current;
        _fadingItem = ctx.CurrentItem;
        _ = ctx.Player.RampPlaybackSourceVolumeAsync(current, 0, remaining - TimeSpan.FromMilliseconds(100));
    }

    public async Task OnTrackEndedAsync(TrackTransitionContext ctx)
    {
        _fadingSource = null;
        _fadingItem = null;
        if (_preloader.IsReady && ReferenceEquals(_preloader.ForItem, ctx.CurrentItem) &&
            ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is AudioGraphPlaybackSource outgoing)
        {
            var next = _preloader.Take()!.Value;
            ctx.Player.PlayPlaybackSource(next.Source);
            _ = ctx.Player.RampPlaybackSourceVolumeAsync(next.Source, TargetVolume(next.Item), FadeInTime);
            if (ctx.Player is AudioGraphPlayer graphPlayer) graphPlayer.PrimaryPlaybackSource = next.Source;
            await ctx.CommitItemAsync(next.Item).ConfigureAwait(false);
            try
            {
                ctx.Player.DisconnectPlaybackSource(outgoing);
            }
            catch
            {
            }
            if (ReferenceEquals(ctx.CurrentItem.PlayItem?.AudioGraphPlaybackSource, outgoing))
            {
                ctx.CurrentItem.PlayItem?.Dispose();
                ctx.CurrentItem.PlayItem = null;
            }
            return;
        }

        _preloader.Discard(ctx.Player);
        var fallbackItem = await ctx.RequestNextItemAsync(true).ConfigureAwait(false);
        if (fallbackItem is not null)
            await ctx.LoadMediaSourceAsync(fallbackItem, true, true, true).ConfigureAwait(false);
    }

    public Task OnManualSkipAsync(TrackTransitionContext ctx)
    {
        _preloader.Discard(ctx.Player);
        _fadingSource = null;
        _fadingItem = null;
        return Task.CompletedTask;
    }

    public void OnSeek(TrackTransitionContext ctx, TimeSpan target)
    {
        if (_fadingSource is not null)
        {
            try
            {
                _ = ctx.Player.RampPlaybackSourceVolumeAsync(_fadingSource, TargetVolume(_fadingItem), TimeSpan.FromMilliseconds(200));
            }
            catch
            {
            }
            _fadingSource = null;
            _fadingItem = null;
        }
        if (_preloader.IsReady && target < ctx.Duration - FadeOutTime - TimeSpan.FromSeconds(15))
            _preloader.Discard(ctx.Player);
    }

    public void Reset()
    {
        _preloader.Discard(_player);
        _fadingSource = null;
        _fadingItem = null;
    }

    private double TargetVolume(HyPlayItem? item) => _setting.EnableAudioGain ? item?.Volume ?? 1d : 1d;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preloader.Dispose();
    }
}
