using System;
using System.Threading.Tasks;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using HyPlayer.UWP.Chopin.Abstractions.Interfaces;
using HyPlayer.UWP.Chopin.Abstractions.Models;

namespace HyPlayer.Services.Playback.Transitions;

/// <summary>
/// 无缝衔接过渡策略（Gapless）。
/// </summary>
public sealed partial class GaplessTransition : ITrackTransition, IDisposable
{
    private readonly Setting _setting;
    private readonly IPlayer _player;
    private readonly NextTrackPreloader _preloader = new();
    private bool _disposed;

    public string Id => "gap";

    public GaplessTransition(Setting setting, IPlayer player)
    {
        _setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public void OnPositionTick(TrackTransitionContext ctx)
    {
        if (_preloader.IsReady && !ReferenceEquals(_preloader.ForItem, ctx.CurrentItem))
            _preloader.Discard(ctx.Player);
        _preloader.TryPreload(ctx, TimeSpan.FromSeconds(20));
    }

    public async Task OnTrackEndedAsync(TrackTransitionContext ctx)
    {
        if (_preloader.IsReady && ReferenceEquals(_preloader.ForItem, ctx.CurrentItem) &&
            ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is AudioGraphPlaybackSource outgoing)
        {
            var next = _preloader.Take()!.Value;
            ctx.Player.SetPlaybackSourceOutputVolume(_setting.EnableAudioGain ? next.Item.Volume ?? 1d : 1d, next.Source);
            ctx.Player.PlayPlaybackSource(next.Source);
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
        var nextItem = await ctx.RequestNextItemAsync(true).ConfigureAwait(false);
        if (nextItem is not null)
            await ctx.LoadMediaSourceAsync(nextItem, true, true, true).ConfigureAwait(false);
    }

    public Task OnManualSkipAsync(TrackTransitionContext ctx)
    {
        _preloader.Discard(ctx.Player);
        return Task.CompletedTask;
    }

    public void OnSeek(TrackTransitionContext ctx, TimeSpan target)
    {
        if (_preloader.IsReady && target < ctx.Duration - TimeSpan.FromSeconds(25))
            _preloader.Discard(ctx.Player);
    }

    public void Reset()
    {
        _preloader.Discard(_player);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preloader.Dispose();
    }
}
