using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using HyPlayer.Services.Playback.Analysis;
using HyPlayer.Services.Playback.AutoMix;
using HyPlayer.Services.Playback.Messages;
using HyPlayer.UWP.Chopin.Abstractions.Interfaces;
using HyPlayer.UWP.Chopin.Abstractions.Models;
using HyPlayer.UWP.Chopin.Dsp;

namespace HyPlayer.Services.Playback.Transitions;

public sealed partial class AutoMixTransition : ITrackTransition, IDisposable
{
    private readonly Setting _setting;
    private readonly IPlayer _player;
    private readonly IAudioAnalysisService _analysis;
    private readonly ILyricService _lyrics;
    private readonly PlaybackStateService _state;
    private readonly NextTrackPreloader _preloader = new();
    private readonly SemaphoreSlim _prepareGate = new(1, 1);

    private HyPlayItem? _analyzedFor;
    private Task<TrackAnalysis?>? _analysisA;
    private Task<TrackAnalysis?>? _analysisB;
    private Task<IReadOnlyList<(double, double)>>? _vocalsA;
    private Task<IReadOnlyList<(double, double)>>? _vocalsB;
    private DateTime _lastAnalysisAttempt;
    private CancellationTokenSource _cts = new();

    private MixPlan? _plan;
    private bool _preparing;
    private AutoMixEngine? _engine;
    private MixRenderer? _renderer;
    private Action? _hook;
    private AudioGraphPlaybackSource? _outgoing;
    private HyPlayItem? _outgoingItem;
    private AudioGraphPlaybackSource? _incoming;
    private HyPlayItem? _incomingItem;
    private bool _committed;
    private bool _mapBusy;
    private bool _planHadVocals;
    private int _mapAFailures;
    private int _mapBFailures;
    private bool _forcedHandBack;
    private bool _watchdogFired;
    private AutoMixStage _lastStage = AutoMixStage.Waiting;
    private bool _fallbackFading;
    private bool _disposed;
    private readonly object _superviseLock = new();
    private TrackTransitionContext? _lastContext;
    private Timer? _superviseTimer;

    public string Id => "mix";

    public AutoMixTransition(Setting setting, IPlayer player, IAudioAnalysisService analysis, ILyricService lyrics, PlaybackStateService state)
    {
        _setting = setting;
        _player = player;
        _analysis = analysis;
        _lyrics = lyrics;
        _state = state;
    }

    private AudioGraphPlayer Graph => (AudioGraphPlayer)_player;

    public void OnPositionTick(TrackTransitionContext ctx)
    {
        if (_engine is not null)
        {
            _lastContext = ctx;
            SuperviseLocked(ctx);
            return;
        }
        if (_fallbackFading) return;

        var current = ctx.CurrentItem;
        if (current is null) return;
        if (!ReferenceEquals(_analyzedFor, current))
        {
            ResetForTrack(ctx);
            _analyzedFor = current;
        }

        if (_preloader.IsReady && !ReferenceEquals(_preloader.ForItem, current))
        {
            _preloader.Discard(ctx.Player);
            _analysisB = null;
            _vocalsB = null;
            _plan = null;
        }

        var remaining = ctx.Duration - ctx.Position;
        RequestAnalyses(ctx, current, remaining);
        _preloader.TryPreload(ctx, TimeSpan.FromSeconds(80));

        if (_plan is not null && !_planHadVocals && !_preparing && _engine is null &&
            _vocalsA is { IsCompleted: true } && _vocalsB is { IsCompleted: true })
            _plan = null;
        if (_plan is null) TryPlan(ctx);
        if (_plan is not null && !_preparing && _engine is null)
        {
            var exitRemaining = ctx.Duration.TotalSeconds - _plan.AExit;
            if (remaining.TotalSeconds <= exitRemaining + 20)
            {
                _preparing = true;
                ctx.TaskRunner.Forget(PrepareAsync(ctx), "prepare automix transition");
            }
        }

        if (_plan is null && remaining <= FallbackFadeTime && remaining > TimeSpan.FromMilliseconds(300) && _preloader.IsReady)
            BeginFallbackFade(ctx, remaining);
    }

    private TimeSpan FallbackFadeTime => TimeSpan.FromSeconds(Math.Clamp(_setting.CrossFadeTime, 2, 10));

    private void RequestAnalyses(TrackTransitionContext ctx, HyPlayItem current, TimeSpan remaining)
    {
        var now = DateTime.UtcNow;
        var retry = (now - _lastAnalysisAttempt).TotalSeconds > 8;
        if ((_analysisA is null || _analysisA.IsCompleted && _analysisA.Result is null && retry) && remaining.TotalSeconds > 30)
        {
            _lastAnalysisAttempt = now;
            _analysisA = _analysis.AnalyzeAsync(current, _cts.Token);
        }
        if (_vocalsA is null && (ctx.Position.TotalSeconds > 30 || remaining.TotalSeconds < 120))
            _vocalsA = SafeVocals(current, true);
        if (!_preloader.IsReady && ctx.Position.TotalSeconds > 20 && remaining.TotalSeconds > 90 && _analysisA is { IsCompleted: true })
            AnalyzeNextAhead(now);
        if (_preloader.IsReady && _preloader.Item is { } next)
        {
            if (_analysisB is null || _analysisB.IsCompleted && _analysisB.Result is null && retry)
            {
                _lastAnalysisAttempt = now;
                _analysisB = _analysis.AnalyzeAsync(next, _cts.Token);
            }
            _vocalsB ??= SafeVocals(next, false);
        }
    }

    private HyPlayItem? _aheadFor;
    private Task<bool>? _aheadTask;
    private DateTime _aheadAttempt;

    private void AnalyzeNextAhead(DateTime now)
    {
        if (_aheadTask is { IsCompleted: false }) return;
        var next = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<IPlaylistService>()?.PeekNextItem();
        if (next is null || ReferenceEquals(next, _analyzedFor)) return;
        var same = ReferenceEquals(next, _aheadFor);
        if (same && _aheadTask is { IsCompletedSuccessfully: true, Result: true }) return;
        if (same && (now - _aheadAttempt).TotalSeconds < 20) return;
        _aheadFor = next;
        _aheadAttempt = now;
        _aheadTask = AnalyzeAheadAsync(next, _cts.Token);
    }

    private async Task<bool> AnalyzeAheadAsync(HyPlayItem next, CancellationToken ct)
    {
        try
        {
            var analysis = await _analysis.AnalyzeAsync(next, ct);
            if (analysis is null) return false;
            await _analysis.AnalyzeVocalsAsync(next, false, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<(double, double)>> SafeVocals(HyPlayItem item, bool outro)
    {
        IReadOnlyList<(double Start, double End)> lyric;
        try
        {
            lyric = await _lyrics.GetVocalRegionsAsync(item, _cts.Token);
        }
        catch
        {
            lyric = Array.Empty<(double, double)>();
        }
        VocalActivity? activity = null;
        try
        {
            activity = await _analysis.AnalyzeVocalsAsync(item, outro, _cts.Token);
        }
        catch
        {
        }
        var fused = VocalActivity.Fuse(lyric, activity);
        if (activity is not null)
        {
            var lyricInWindow = lyric.Count(r => r.End > activity.Start && r.Start < activity.End);
            var inWindow = fused.Where(r => r.End > activity.Start && r.Start < activity.End);
            AutoMixLog.Write($"vocals {(outro ? "outro" : "intro")} {item.Name} {activity.Start:0.0}-{activity.End:0.0}s: lyrics {lyricInWindow} lines, model {Describe(activity.Regions(0.12, 0.06))} -> {Describe(inWindow)}");
        }
        return fused.Select(r => (r.Start, r.End)).ToList();
    }

    private static string Describe(IEnumerable<(double Start, double End)> regions) =>
        string.Join(" ", regions.Select(r => $"[{r.Start:0.0}-{r.End:0.0}]"));

    private void TryPlan(TrackTransitionContext ctx)
    {
        if (Math.Abs(_state.PlaybackSpeed - 1) > 0.001) return;
        if ((_vocalsA is { IsCompleted: false } || _vocalsB is { IsCompleted: false }) && (ctx.Duration - ctx.Position).TotalSeconds > 40) return;
        if (_analysisA is not { IsCompletedSuccessfully: true } || _analysisA.Result is not { } a) return;
        if (_analysisB is not { IsCompletedSuccessfully: true } || _analysisB.Result is not { } b) return;
        var vocalsA = _vocalsA is { IsCompletedSuccessfully: true } ? _vocalsA.Result : Array.Empty<(double, double)>();
        var vocalsB = _vocalsB is { IsCompletedSuccessfully: true } ? _vocalsB.Result : Array.Empty<(double, double)>();
        var current = ctx.CurrentItem;
        var next = _preloader.Item;
        var album = current?.Album?.Id is { Length: > 0 } albumA && albumA != "0" && next?.Album?.Id == albumA;
        AutoMixLog.Write($"analysis A {current?.Name}: bpm {a.Bpm:0.0} conf {a.BeatConfidence:0.00} beats {a.Beats.Length} lufs {a.IntegratedLoudness:0.0} sound {a.SoundStart:0.0}-{a.SoundEnd:0.0} outro {a.OutroStart:0.0} key {a.Key} vocals {vocalsA.Count} by {a.BeatAnalyzer}/{a.KeyAnalyzer}/{_analysis.CurrentVocalAnalyzerId ?? "lyrics"}");
        AutoMixLog.Write($"analysis B {next?.Name}: bpm {b.Bpm:0.0} conf {b.BeatConfidence:0.00} beats {b.Beats.Length} lufs {b.IntegratedLoudness:0.0} sound {b.SoundStart:0.0}-{b.SoundEnd:0.0} intro {b.IntroEnd:0.0} key {b.Key} vocals {vocalsB.Count} by {b.BeatAnalyzer}/{b.KeyAnalyzer}/{_analysis.CurrentVocalAnalyzerId ?? "lyrics"}");
        _planHadVocals = _vocalsA is { IsCompleted: true } && _vocalsB is { IsCompleted: true };
        _plan = AutoMixPlanner.Plan(a, b, vocalsA, vocalsB, new AutoMixPlannerOptions
        {
            MaxOverlapSeconds = Math.Clamp(_setting.AutoMixMaxOverlap, 8, 60),
            CrossfadeSeconds = Math.Clamp(_setting.CrossFadeTime, 2, 12),
            AllowTimeStretch = _setting.AutoMixTimeStretch && TimeStretcher.IsAvailable,
            AlbumContinuation = album && _setting.AutoMixKeepAlbumGapless
        });
        AutoMixLog.Write($"plan {_plan.Technique}: exit A {_plan.AExit:0.00}s overlap {_plan.Overlap:0.00}s entry B {_plan.BEntry:0.00}s rate {_plan.Rate:0.0000} ramp {_plan.RampBack:0.0}s gainB {_plan.GainB:0.00} ({_plan.Reason}) stretch {TimeStretcher.IsAvailable}");
    }

    private async Task PrepareAsync(TrackTransitionContext ctx)
    {
        await _prepareGate.WaitAsync();
        try
        {
            var plan = _plan;
            var current = ctx.CurrentItem;
            var next = _preloader.Item;
            if (plan is null || current is null || next is null) return;
            if (current.PlayItem?.AudioGraphPlaybackSource is not AudioGraphPlaybackSource outgoing) return;
            if (_preloader.Source is not AudioGraphPlaybackSource incoming) return;
            var rate = (int)Graph.SampleRate;
            if (rate <= 0) return;

            var handoverStart = Math.Max(0, plan.AExit - 10);
            using var streamA = await _analysis.OpenLocalAudioAsync(current);
            using var streamB = await _analysis.OpenLocalAudioAsync(next);
            if (streamA is null || streamB is null) return;
            var pcmA = await AudioDecoder.DecodeAsync(streamA, (uint)rate, 2, TimeSpan.FromSeconds(handoverStart), TimeSpan.Zero, _cts.Token);
            var bLength = plan.BEntry + plan.Overlap * plan.Rate + plan.RampBack * Math.Max(1, plan.Rate) + 20;
            var bStart = Math.Max(0, plan.BEntry - 1);
            var pcmB = await AudioDecoder.DecodeAsync(streamB, (uint)rate, 2, TimeSpan.FromSeconds(bStart), TimeSpan.FromSeconds(bLength - bStart), _cts.Token);
            var regionA = new MixRegion { Samples = pcmA.Samples, SampleRate = pcmA.SampleRate, StartSeconds = handoverStart };
            var regionB = new MixRegion { Samples = pcmB.Samples, SampleRate = pcmB.SampleRate, StartSeconds = bStart };
            if (regionA.Frames < rate * 3 || regionB.Frames < rate * 3) return;
            if (!ReferenceEquals(ctx.CurrentItem, _analyzedFor) || !ReferenceEquals(_preloader.Item, next)) return;

            var volumeA = _setting.EnableAudioGain ? current.Volume ?? 1d : 1d;
            var volumeB = _setting.EnableAudioGain ? next.Volume ?? 1d : 1d;
            _regionA = regionA;
            _regionB = regionB;
            var engine = new AutoMixEngine(new GraphMixHost(Graph), plan, regionA, regionB, outgoing, incoming, volumeA, volumeB)
            {
                TapA = Graph.AttachTap(outgoing)
            };
            _player.SetPlaybackSourceOutputVolume(0, incoming);
            _player.SeekPlaybackSource(TimeSpan.FromSeconds(plan.BEntry), incoming);
            _outgoing = outgoing;
            _outgoingItem = current;
            _incoming = incoming;
            _incomingItem = next;
            _preloader.Take();
            _committed = false;
            _engine = engine;
            _mapAFailures = 0;
            _mapBFailures = 0;
            _forcedHandBack = false;
            _watchdogFired = false;
            _lastStage = AutoMixStage.Waiting;
            _renderer = Graph.CreateMixRenderer(engine);
            _hook = engine.OnQuantum;
            Graph.AddQuantumHook(_hook);
            StartSupervisor(ctx);
            AutoMixLog.Write($"prepared: region A {regionA.StartSeconds:0.00}+{regionA.Frames / (double)rate:0.0}s region B {regionB.StartSeconds:0.00}+{regionB.Frames / (double)rate:0.0}s graph {rate}Hz");
            Graph.OnGraphRebuilt += OnGraphRebuilt;
        }
        catch (Exception ex)
        {
            AutoMixLog.Write("prepare failed: " + ex.Message);
        }
        finally
        {
            _preparing = false;
            _prepareGate.Release();
        }
    }

    private void SuperviseLocked(TrackTransitionContext ctx)
    {
        if (!Monitor.TryEnter(_superviseLock)) return;
        try
        {
            if (_engine is not null) Supervise(ctx);
        }
        catch (Exception ex)
        {
            AutoMixLog.Write("supervise failed: " + ex.Message);
        }
        finally
        {
            Monitor.Exit(_superviseLock);
        }
    }

    private void StartSupervisor(TrackTransitionContext ctx)
    {
        _lastContext = ctx;
        _superviseTimer ??= new Timer(_ =>
        {
            if (_engine is not null && _lastContext is { } context) SuperviseLocked(context);
        }, null, 200, 200);
    }

    private void StopSupervisor()
    {
        Interlocked.Exchange(ref _superviseTimer, null)?.Dispose();
    }

    private void Supervise(TrackTransitionContext ctx)
    {
        var engine = _engine!;
        if (engine.Stage == AutoMixStage.Waiting && double.IsNaN(engine.MapA) && !_mapBusy && engine.TapA is { } tapA && _outgoing is not null &&
            _regionA is { } regionA && ctx.Position.TotalSeconds >= regionA.StartSeconds + 0.4)
        {
            _mapBusy = true;
            var expected = (long)Math.Round((ctx.Position.TotalSeconds - regionA.StartSeconds) * Graph.SampleRate);
            ctx.TaskRunner.Forget(async () =>
            {
                try
                {
                    await Task.Delay(400);
                    var map = await Task.Run(() => TapAligner.FindMap(tapA, regionA, expected + (long)(Graph.SampleRate * 0.4)));
                    if (map is not null)
                    {
                        engine.MapA = map.Value;
                        var actual = regionA.StartSeconds + (tapA.TotalFrames + map.Value) / Graph.SampleRate;
                        AutoMixLog.Write($"map A: {map.Value:0} after {_mapAFailures} misses, position {ctx.Position.TotalSeconds:0.00}s plays content {actual:0.00}s");
                    }
                    else if (_mapAFailures++ == 0)
                    {
                        AutoMixLog.Write("map A: not found, retrying");
                    }
                }
                finally
                {
                    _mapBusy = false;
                }
            }, "align automix outgoing");
        }

        if (engine.BStarted && !_state.IsTransitioning && !engine.OutgoingFinished)
            SetTransitioning(true);

        if (engine.MidpointReached && !_committed) Commit(ctx);

        if (engine.OutgoingFinished && _outgoing is not null)
        {
            ReleaseOutgoing(ctx.Player);
            SetTransitioning(false);
        }

        if (engine.NeedsMapB && double.IsNaN(engine.MapB) && !_mapBusy && _incoming is not null)
        {
            engine.TapB ??= Graph.AttachTap(_incoming);
            _mapBusy = true;
            var tapB = engine.TapB;
            ctx.TaskRunner.Forget(async () =>
            {
                try
                {
                    await Task.Delay(600);
                    var expected = (long)Math.Round(engine.BContentIndex);
                    if (_regionB is not { } regionB) return;
                    var map = await Task.Run(() => TapAligner.FindMap(tapB, regionB, expected));
                    if (map is not null) engine.MapB = map.Value;
                    else _mapBFailures++;
                    AutoMixLog.Write($"map B: {(map is null ? "not found" : map.Value.ToString("0"))}");
                }
                finally
                {
                    _mapBusy = false;
                }
            }, "align automix incoming");
        }

        TryForceHandBack(engine);

        if (engine.Stage != _lastStage)
        {
            AutoMixLog.Write($"stage {_lastStage} -> {engine.Stage} at A/B pos {ctx.Position.TotalSeconds:0.00}s progress {engine.Progress:0.00}");
            _lastStage = engine.Stage;
        }
        if (engine.Stage is AutoMixStage.Done or AutoMixStage.Aborted) FinishEngine();
    }

    private void TryForceHandBack(AutoMixEngine engine)
    {
        if (engine.Stage is AutoMixStage.Done or AutoMixStage.Aborted) return;
        var limit = engine.Plan.Overlap + engine.Plan.RampBack + 16;
        var elapsed = engine.SecondsSinceBStarted;
        if (elapsed > limit + 8 || _incoming is null && engine.BStarted)
        {
            if (!_watchdogFired) AutoMixLog.Write($"watchdog abort at stage {engine.Stage} after {elapsed:0.0}s");
            _watchdogFired = true;
            engine.RequestAbort();
            return;
        }
        if (_forcedHandBack || _incoming is null) return;
        if (engine.Stage is not (AutoMixStage.RampBack or AutoMixStage.Aligning)) return;
        string? reason = engine.NeedsForcedHandBack ? "region exhausted"
            : engine.NeedsMapB && _mapBFailures >= 3 ? "map B failed"
            : elapsed > limit ? "watchdog" : null;
        if (reason is null) return;
        var position = engine.BContentSeconds;
        if (double.IsNaN(position)) return;
        _forcedHandBack = true;
        AutoMixLog.Write($"forced hand-back ({reason}) at B {position:0.00}s");
        try
        {
            _player.SetPlaybackSourceSpeed(1, _incoming);
            _player.SeekPlaybackSource(TimeSpan.FromSeconds(position + 0.03), _incoming);
            _ = _player.RampPlaybackSourceVolumeAsync(_incoming, engine.VolumeB, TimeSpan.FromMilliseconds(60));
        }
        catch
        {
        }
        engine.BeginForcedHandBack(0.06);
    }

    private MixRegion? _regionA;
    private MixRegion? _regionB;

    private void Commit(TrackTransitionContext ctx)
    {
        if (_committed || _incoming is null || _incomingItem is null) return;
        _committed = true;
        Graph.PrimaryPlaybackSource = _incoming;
        ctx.TaskRunner.Forget(ctx.CommitItemAsync(_incomingItem), "commit automix item");
    }

    private void ReleaseOutgoing(IPlayer player)
    {
        if (_engine?.TapA is { } tapA)
        {
            Graph.DetachTap(_outgoing, tapA);
            _engine.TapA = null;
        }
        if (_outgoing is null) return;
        try
        {
            player.DisconnectPlaybackSource(_outgoing);
        }
        catch
        {
        }
        if (_outgoingItem is not null && ReferenceEquals(_outgoingItem.PlayItem?.AudioGraphPlaybackSource, _outgoing))
        {
            _outgoingItem.PlayItem?.Dispose();
            _outgoingItem.PlayItem = null;
        }
        _outgoing = null;
        _outgoingItem = null;
    }

    private void FinishEngine()
    {
        if (_hook is not null) Graph.RemoveQuantumHook(_hook);
        _hook = null;
        Graph.OnGraphRebuilt -= OnGraphRebuilt;
        var renderer = _renderer;
        var engine = _engine;
        _renderer = null;
        _engine = null;
        if (engine?.TapA is { } tapA)
        {
            Graph.DetachTap(_outgoing, tapA);
            engine.TapA = null;
        }
        if (engine?.TapB is { } tapB)
        {
            Graph.DetachTap(_incoming, tapB);
            engine.TapB = null;
        }
        if (engine is not null && engine.Stage == AutoMixStage.Aborted && _incoming is not null && !_committed)
        {
            try
            {
                _player.DisconnectPlaybackSource(_incoming);
            }
            catch
            {
            }
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            if (renderer is not null) Graph.ReleaseMixRenderer(renderer);
            engine?.Dispose();
        });
        _incoming = null;
        _incomingItem = null;
        _plan = null;
        _regionA = null;
        _regionB = null;
        _committed = false;
        StopSupervisor();
        SetTransitioning(false);
    }

    private void OnGraphRebuilt()
    {
        _engine?.RequestAbort();
    }

    private void BeginFallbackFade(TrackTransitionContext ctx, TimeSpan remaining)
    {
        if (ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is not AudioGraphPlaybackSource outgoing) return;
        var next = _preloader.Take();
        if (next is null) return;
        _fallbackFading = true;
        _committed = false;
        var duration = remaining < FallbackFadeTime ? remaining : FallbackFadeTime;
        var target = _setting.EnableAudioGain ? next.Value.Item.Volume ?? 1d : 1d;
        ctx.Player.PlayPlaybackSource(next.Value.Source);
        _ = ctx.Player.RampPlaybackSourceVolumeAsync(next.Value.Source, target, duration);
        var fadeOut = ctx.Player.RampPlaybackSourceVolumeAsync(outgoing, 0, duration);
        _incoming = next.Value.Source;
        _incomingItem = next.Value.Item;
        _outgoing = outgoing;
        _outgoingItem = ctx.CurrentItem;
        SetTransitioning(true);
        ctx.TaskRunner.Forget(async () =>
        {
            await Task.Delay(duration / 2);
            Commit(ctx);
            await fadeOut;
            ReleaseOutgoing(ctx.Player);
            _incoming = null;
            _incomingItem = null;
            _committed = false;
            _fallbackFading = false;
            SetTransitioning(false);
        }, "automix fallback crossfade");
    }

    public async Task OnTrackEndedAsync(TrackTransitionContext ctx)
    {
        if (_engine is not null)
        {
            Commit(ctx);
            return;
        }
        if (_fallbackFading)
        {
            Commit(ctx);
            return;
        }
        if (_preloader.IsReady && ReferenceEquals(_preloader.ForItem, ctx.CurrentItem) &&
            ctx.CurrentItem?.PlayItem?.AudioGraphPlaybackSource is AudioGraphPlaybackSource outgoing)
        {
            var next = _preloader.Take()!.Value;
            ctx.Player.SetPlaybackSourceOutputVolume(_setting.EnableAudioGain ? next.Item.Volume ?? 1d : 1d, next.Source);
            ctx.Player.PlayPlaybackSource(next.Source);
            Graph.PrimaryPlaybackSource = next.Source;
            await ctx.CommitItemAsync(next.Item).ConfigureAwait(false);
            try
            {
                ctx.Player.DisconnectPlaybackSource(outgoing);
            }
            catch
            {
            }
            return;
        }
        _preloader.Discard(ctx.Player);
        var fallback = await ctx.RequestNextItemAsync(true).ConfigureAwait(false);
        if (fallback is not null) await ctx.LoadMediaSourceAsync(fallback, true, true, true).ConfigureAwait(false);
    }

    public async Task OnManualSkipAsync(TrackTransitionContext ctx)
    {
        await AbortAsync(ctx.Player);
        _preloader.Discard(ctx.Player);
        ResetForTrack(ctx);
    }

    public void OnSeek(TrackTransitionContext ctx, TimeSpan target)
    {
        if (_engine is not null)
        {
            _ = AbortAsync(ctx.Player);
            return;
        }
        if (_preloader.IsReady && target < ctx.Duration - TimeSpan.FromSeconds(90))
        {
            _preloader.Discard(ctx.Player);
            _plan = null;
            _analysisB = null;
        }
    }

    private async Task AbortAsync(IPlayer player)
    {
        var engine = _engine;
        if (engine is null) return;
        engine.RequestAbort();
        for (var i = 0; i < 20 && engine.Stage != AutoMixStage.Aborted && engine.Stage != AutoMixStage.Done; i++)
            await Task.Delay(15);
        if (_committed) ReleaseOutgoing(player);
        else
        {
            if (_engine?.TapA is { } tapA)
            {
                Graph.DetachTap(_outgoing, tapA);
                _engine.TapA = null;
            }
            _outgoing = null;
            _outgoingItem = null;
        }
        FinishEngine();
    }

    private void SetTransitioning(bool value)
    {
        if (_state.IsTransitioning == value) return;
        _state.IsTransitioning = value;
        WeakReferenceMessenger.Default.Send(new TransitionStateChangedMessage(value, value ? _incomingItem : null));
    }

    private void ResetForTrack(TrackTransitionContext ctx)
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _analysisA = null;
        _analysisB = null;
        _vocalsA = null;
        _vocalsB = null;
        _plan = null;
        _analyzedFor = null;
        _aheadFor = null;
        _aheadTask = null;
    }

    public void Reset()
    {
        if (_engine is not null) _ = AbortAsync(_player);
        _preloader.Discard(_player);
        _analysisA = null;
        _analysisB = null;
        _vocalsA = null;
        _vocalsB = null;
        _plan = null;
        _analyzedFor = null;
        _aheadFor = null;
        _aheadTask = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSupervisor();
        _cts.Cancel();
        _preloader.Dispose();
        _prepareGate.Dispose();
    }
}
