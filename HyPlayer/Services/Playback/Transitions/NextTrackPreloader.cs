using System;
using System.Threading;
using System.Threading.Tasks;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using HyPlayer.UWP.Chopin.Abstractions.Interfaces;
using HyPlayer.UWP.Chopin.Abstractions.Models;

namespace HyPlayer.Services.Playback.Transitions;

internal sealed class NextTrackPreloader : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _busy;
    private int _generation;

    public HyPlayItem? Item { get; private set; }
    public AudioGraphPlaybackSource? Source { get; private set; }
    public HyPlayItem? ForItem { get; private set; }
    public bool IsReady => Source is not null && Item is not null;

    public void TryPreload(TrackTransitionContext ctx, TimeSpan leadTime)
    {
        if (_busy || IsReady || ctx.CurrentItem is null) return;
        var remaining = ctx.Duration - ctx.Position;
        if (ctx.Duration <= TimeSpan.FromSeconds(10) || remaining > leadTime) return;
        ctx.TaskRunner.Forget(PreloadAsync(ctx), "preload next track");
    }

    private async Task PreloadAsync(TrackTransitionContext ctx)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        _busy = true;
        var generation = Volatile.Read(ref _generation);
        try
        {
            if (IsReady) return;
            var current = ctx.CurrentItem;
            var next = await ctx.RequestNextItemAsync(false).ConfigureAwait(false);
            if (next is null || ReferenceEquals(next, current)) return;

            await ctx.LoadMediaSourceAsync(next, false, false, false).ConfigureAwait(false);
            if (next.PlayItem?.AudioGraphPlaybackSource is not AudioGraphPlaybackSource source) return;
            if (generation != Volatile.Read(ref _generation))
            {
                DiscardSource(ctx.Player, next, source);
                return;
            }

            ctx.Player.SetPlaybackSourceOutputVolume(0, source);
            Item = next;
            Source = source;
            ForItem = current;
        }
        catch
        {
        }
        finally
        {
            _busy = false;
            _gate.Release();
        }
    }

    public (HyPlayItem Item, AudioGraphPlaybackSource Source)? Take()
    {
        if (Item is null || Source is null) return null;
        var result = (Item, Source);
        Item = null;
        Source = null;
        ForItem = null;
        return result;
    }

    public void Discard(IPlayer player)
    {
        Interlocked.Increment(ref _generation);
        if (Item is not null && Source is not null) DiscardSource(player, Item, Source);
        Item = null;
        Source = null;
        ForItem = null;
    }

    private static void DiscardSource(IPlayer player, HyPlayItem item, AudioGraphPlaybackSource source)
    {
        try
        {
            player.DisconnectPlaybackSource(source);
        }
        catch
        {
        }
        if (ReferenceEquals(item.PlayItem?.AudioGraphPlaybackSource, source))
        {
            item.PlayItem?.Dispose();
            item.PlayItem = null;
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
