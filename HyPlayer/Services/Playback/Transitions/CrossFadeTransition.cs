using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using HyPlayer.Services.Playback.Messages;
using HyPlayer.UWP.Chopin.Abstractions.Interfaces;
using HyPlayer.UWP.Chopin.Abstractions.Models;

namespace HyPlayer.Services.Playback.Transitions;

/// <summary>
/// 交叉淡入淡出过渡策略。
/// <para>
/// 在当前曲目即将结束时预加载下一首，并在剩余时间 ≤ <see cref="Setting.CrossFadeTime"/> 时
/// 同时对当前曲目执行淡出、对下一曲目执行淡入，实现平滑过渡。
/// </para>
/// </summary>
public sealed partial class CrossFadeTransition : ITrackTransition, IDisposable
{
    private readonly Setting _setting;
    private readonly IPlayer _player;
    private readonly PlaybackStateService _state;
    private readonly NextTrackPreloader _preloader = new();

    private AudioGraphPlaybackSource? _outgoing;
    private HyPlayItem? _outgoingItem;
    private AudioGraphPlaybackSource? _incoming;
    private HyPlayItem? _incomingItem;
    private TimeSpan _fadeStartPosition;
    private TimeSpan _fadeDuration;
    private bool _fading;
    private bool _committed;
    private int _fadeVersion;
    private bool _disposed;

    /// <inheritdoc />
    public string Id => "xfd";

    public CrossFadeTransition(Setting setting, IPlayer player, PlaybackStateService state)
    {
        _setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    private TimeSpan FadeTime => TimeSpan.FromSeconds(Math.Clamp(_setting.CrossFadeTime, 1, 12));

    public void OnPositionTick(TrackTransitionContext ctx)
    {
        if (_fading)
        {
            if (!_committed && ctx.Position - _fadeStartPosition >= _fadeDuration / 2)
                Commit(ctx);
            return;
        }

        if (_preloader.IsReady && !ReferenceEquals(_preloader.ForItem, ctx.CurrentItem))
            _preloader.Discard(ctx.Player);

        _preloader.TryPreload(ctx, FadeTime + TimeSpan.FromSeconds(15));

        var remaining = ctx.Duration - ctx.Position;
        if (!_preloader.IsReady || remaining > FadeTime || remaining < TimeSpan.FromMilliseconds(300)) return;
        BeginFade(ctx, remaining);
    }

    private void BeginFade(TrackTransitionContext ctx, TimeSpan remaining)
    {
        if (ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is not AudioGraphPlaybackSource outgoing) return;
        var next = _preloader.Take();
        if (next is null) return;

        _outgoing = outgoing;
        _outgoingItem = ctx.CurrentItem;
        _incoming = next.Value.Source;
        _incomingItem = next.Value.Item;
        _fadeDuration = remaining < FadeTime ? remaining : FadeTime;
        _fadeStartPosition = ctx.Position;
        _fading = true;
        _committed = false;
        var version = ++_fadeVersion;
        SetTransitioning(true);

        var target = TargetVolume(_incomingItem);
        ctx.Player.PlayPlaybackSource(_incoming);
        _ = ctx.Player.RampPlaybackSourceVolumeAsync(_incoming, target, _fadeDuration);
        var fadeOut = ctx.Player.RampPlaybackSourceVolumeAsync(_outgoing, 0, _fadeDuration);
        ctx.TaskRunner.Forget(FinishWhenDoneAsync(ctx, fadeOut, version), "cross-fade completion");
    }

    private async Task FinishWhenDoneAsync(TrackTransitionContext ctx, Task<bool> fadeOut, int version)
    {
        var completed = await fadeOut.ConfigureAwait(false);
        if (!completed || version != _fadeVersion || !_fading) return;
        if (!_committed) Commit(ctx);
        ReleaseOutgoing(ctx);
        _fading = false;
        SetTransitioning(false);
    }

    private void SetTransitioning(bool value)
    {
        if (_state.IsTransitioning == value) return;
        _state.IsTransitioning = value;
        WeakReferenceMessenger.Default.Send(new TransitionStateChangedMessage(value, value ? _incomingItem : null));
    }

    private void Commit(TrackTransitionContext ctx)
    {
        if (_committed || _incoming is null || _incomingItem is null) return;
        _committed = true;
        if (ctx.Player is AudioGraphPlayer graphPlayer) graphPlayer.PrimaryPlaybackSource = _incoming;
        ctx.TaskRunner.Forget(ctx.CommitItemAsync(_incomingItem), "commit cross-fade item");
    }

    private void ReleaseOutgoing(TrackTransitionContext ctx)
    {
        if (_outgoing is not null)
        {
            try
            {
                ctx.Player.DisconnectPlaybackSource(_outgoing);
            }
            catch
            {
            }
        }
        if (_outgoingItem is not null && ReferenceEquals(_outgoingItem.PlayItem?.AudioGraphPlaybackSource, _outgoing))
        {
            _outgoingItem.PlayItem?.Dispose();
            _outgoingItem.PlayItem = null;
        }
        _outgoing = null;
        _outgoingItem = null;
    }

    public async Task OnTrackEndedAsync(TrackTransitionContext ctx)
    {
        if (_fading)
        {
            Commit(ctx);
            _fadeVersion++;
            ReleaseOutgoing(ctx);
            if (_incoming is not null)
                _ = ctx.Player.RampPlaybackSourceVolumeAsync(_incoming, TargetVolume(_incomingItem), TimeSpan.FromMilliseconds(250));
            _fading = false;
            SetTransitioning(false);
            return;
        }

        if (_preloader.IsReady && ReferenceEquals(_preloader.ForItem, ctx.CurrentItem) &&
            ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is AudioGraphPlaybackSource outgoing)
        {
            var next = _preloader.Take()!.Value;
            _outgoing = outgoing;
            _outgoingItem = ctx.CurrentItem;
            _incoming = next.Source;
            _incomingItem = next.Item;
            _committed = false;
            ctx.Player.PlayPlaybackSource(_incoming);
            _ = ctx.Player.RampPlaybackSourceVolumeAsync(_incoming, TargetVolume(_incomingItem), TimeSpan.FromMilliseconds(250));
            Commit(ctx);
            ReleaseOutgoing(ctx);
            return;
        }

        _preloader.Discard(ctx.Player);
        var fallbackItem = await ctx.RequestNextItemAsync(true).ConfigureAwait(false);
        if (fallbackItem is not null)
            await ctx.LoadMediaSourceAsync(fallbackItem, true, true, true).ConfigureAwait(false);
    }

    public Task OnManualSkipAsync(TrackTransitionContext ctx)
    {
        _fadeVersion++;
        _preloader.Discard(ctx.Player);
        if (_fading)
        {
            if (_incoming is not null)
            {
                try
                {
                    ctx.Player.DisconnectPlaybackSource(_incoming);
                }
                catch
                {
                }
                if (_incomingItem is not null && ReferenceEquals(_incomingItem.PlayItem?.AudioGraphPlaybackSource, _incoming))
                {
                    _incomingItem.PlayItem?.Dispose();
                    _incomingItem.PlayItem = null;
                }
            }
            ReleaseOutgoing(ctx);
        }
        ResetInternal();
        return Task.CompletedTask;
    }

    public void OnSeek(TrackTransitionContext ctx, TimeSpan target)
    {
        if (!_fading)
        {
            if (_preloader.IsReady && target < ctx.Duration - FadeTime - TimeSpan.FromSeconds(15))
                _preloader.Discard(ctx.Player);
            return;
        }

        _fadeVersion++;
        if (_committed)
        {
            ReleaseOutgoing(ctx);
            if (_incoming is not null)
                _ = ctx.Player.RampPlaybackSourceVolumeAsync(_incoming, TargetVolume(_incomingItem), TimeSpan.FromMilliseconds(200));
        }
        else
        {
            if (_incoming is not null)
            {
                try
                {
                    ctx.Player.DisconnectPlaybackSource(_incoming);
                }
                catch
                {
                }
                if (_incomingItem is not null && ReferenceEquals(_incomingItem.PlayItem?.AudioGraphPlaybackSource, _incoming))
                {
                    _incomingItem.PlayItem?.Dispose();
                    _incomingItem.PlayItem = null;
                }
            }
            if (_outgoing is not null)
                _ = ctx.Player.RampPlaybackSourceVolumeAsync(_outgoing, TargetVolume(_outgoingItem), TimeSpan.FromMilliseconds(200));
        }
        ResetInternal();
    }

    /// <summary>
    /// 重置所有内部状态。
    /// </summary>
    public void Reset()
    {
        _fadeVersion++;
        _preloader.Discard(_player);
        ResetInternal();
    }

    private double TargetVolume(HyPlayItem? item) => _setting.EnableAudioGain ? item?.Volume ?? 1d : 1d;

    private void ResetInternal()
    {
        if (_fading) SetTransitioning(false);
        _fading = false;
        _committed = false;
        _outgoing = null;
        _outgoingItem = null;
        _incoming = null;
        _incomingItem = null;
        _fadeStartPosition = TimeSpan.Zero;
        _fadeDuration = TimeSpan.Zero;
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preloader.Dispose();
        ResetInternal();
    }
}
