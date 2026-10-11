using HyPlayer.UWP.Chopin.Abstractions.Interfaces;
using HyPlayer.UWP.Chopin.Dsp;
using HyPlayer.UWP.Chopin.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Effects;
using Timer = System.Timers.Timer;

namespace HyPlayer.UWP.Chopin.Abstractions.Models
{
    /// <summary>
    /// 基于 AudioGraph 的音频播放器实现
    /// 提供多音轨播放、设备切换、音量控制等功能
    /// </summary>
    public partial class AudioGraphPlayer : IPlayer, IDisposable
    {
        #region Private Fields
        private readonly ConcurrentDictionary<AudioGraphPlaybackSource, MediaSourceAudioInputNode> _audioInputNodes = new();
        private readonly ConcurrentDictionary<MediaSourceAudioInputNode, AudioGraphPlaybackSource> _audioInputNodesReverseDictionary = new();
        private readonly ConcurrentDictionary<AudioGraphPlaybackSource, AudioSubmixNode> _voiceBuses = new();
        private AudioGraph _defaultPlayer;
        private AudioDeviceOutputNode _outputNode;
        private AudioFrameOutputNode _frameOutputNode;
        private bool _disposedValue;
        private readonly Timer _positionTimer = new() { AutoReset = true, Interval = 100 };
        private TimeSpan _lastPosition = TimeSpan.Zero;
        private string _currentDeviceId = string.Empty;
        private double _volume = 1;
        private bool _muted;
        private double _masterFade = 1;
        private AudioGraphPlaybackSource _primaryPlaybackSource;
        private readonly Lock _rampLock = new();
        private readonly List<GainRamp> _ramps = new();
        private GainRamp _masterRamp;
        private int _transportVersion;
        private readonly List<PlaybackTap> _taps = new();
        private readonly List<MixRenderer> _renderers = new();
        private readonly List<Action> _quantumHooks = new();
        private long _quantumIndex;
        private readonly object _callbackLock = new();
        private AudioGraph _callbackGraph;
        private AudioFrameOutputNode _runningFrameOutput;
        private readonly Dictionary<PlaybackTap, AudioGraphPlaybackSource> _tapSources = new();
        private readonly CallbackGate _callbackGate = new();
        [ThreadStatic] private static bool _inGraphCallback;
        #endregion

        private sealed class GainRamp
        {
            public AudioGraphPlaybackSource Source;
            public double From;
            public double To;
            public double Duration;
            public double Elapsed;
            public FadeCurve Curve;
            public TaskCompletionSource<bool> Completion;
        }

        #region Public Properties
        public bool PlayerCreated => _defaultPlayer != null;
        public double Volume => _volume;
        public uint SampleRate => _defaultPlayer?.EncodingProperties.SampleRate ?? 0;

        public bool IsMuted
        {
            get => _outputNode != null && _muted;
            set
            {
                if (_outputNode == null) return;
                _muted = value;
                ApplyMasterGain();
            }
        }

        public FFTProcessor FFTProcessor = new();

        public IPlaybackSource PrimaryPlaybackSource
        {
            get => _primaryPlaybackSource;
            set
            {
                var source = value as AudioGraphPlaybackSource;
                if (ReferenceEquals(source, _primaryPlaybackSource)) return;
                _primaryPlaybackSource = source;
                OnPrimaryPlaybackSourceChanged?.Invoke(source);
            }
        }

        public PlaybackStatus GlobalPlaybackStatus { get; protected set; } = PlaybackStatus.Closed;

        public MediaSourceAudioInputNode PrimaryAudioInputNode
        {
            get
            {
                if (PrimaryPlaybackSource == null) return null;
                var source = PrimaryPlaybackSource as AudioGraphPlaybackSource;
                return _audioInputNodes.TryGetValue(source, out var node) ? node : null;
            }
        }

        public int ConnectedPlaybackSourceCount => _audioInputNodes.Count;
        public ISMTCManager SMTCManager { get; set; }
        private bool _enableFftProcessing;

        public bool EnableFFTProcessing
        {
            get => _enableFftProcessing;
            set
            {
                _enableFftProcessing = value;
                RefreshCallbacks();
            }
        }

        #endregion

        #region Events
        public delegate void PositionChangeHandler(TimeSpan position);
        public event PositionChangeHandler OnPositionChanged;

        public delegate void TrackReachesEndHandler(IPlaybackSource source);
        public event TrackReachesEndHandler OnTrackReachesEnd;

        public delegate void PlaybackSourceStatusChangeHandler(IPlaybackSource source, PlaybackStatus status);
        public event PlaybackSourceStatusChangeHandler OnPlaybackSourceStatusChanged;

        public delegate void GlobalPlaybackStatusChangeHandler(PlaybackStatus status);
        public event GlobalPlaybackStatusChangeHandler OnGlobalPlaybackStatusChanged;

        public delegate void PrimaryPlaybackSourceChangeHandler(IPlaybackSource source);
        public event PrimaryPlaybackSourceChangeHandler OnPrimaryPlaybackSourceChanged;
        #endregion

        #region Initialization and Cleanup
        public async Task InitializePlayer(IAudioSettings settings)
        {
            if (settings is not AudioGraphAudioSetting audioGraphSetting)
                throw new ArgumentException("Setting is not AudioGraphSetting");

            _positionTimer.Elapsed += PositionTimer_Elapsed;

            // 创建 AudioGraph 和输出节点
            var (graph, outputNode) = await CreateGraphAsync(audioGraphSetting);
            _defaultPlayer = graph;
            _outputNode = outputNode;
            _defaultPlayer.UnrecoverableErrorOccurred += GraphOnUnrecoverableErrorOccurred;
            var encodingProperties = _outputNode.EncodingProperties.Copy();
            encodingProperties.ChannelCount = 1;
            var frameOutputResult = _defaultPlayer.CreateFrameOutputNode(encodingProperties);
            _frameOutputNode = frameOutputResult;
            EnableFFTProcessing = settings.EnableFFTProcessing;
            _volume = audioGraphSetting.OutputVolume;
            ApplyMasterGain();
            _currentDeviceId = audioGraphSetting.ResolvedDeviceId;
            _positionTimer.Start();
        }

        public event Action<AudioGraphUnrecoverableError> OnDeviceLost;

        private void GraphOnUnrecoverableErrorOccurred(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
        {
            if (!ReferenceEquals(sender, _defaultPlayer)) return;
            _currentDeviceId = string.Empty;
            OnDeviceLost?.Invoke(args.Error);
        }

        private static async Task<(AudioGraph graph, AudioDeviceOutputNode output)> CreateGraphAsync(AudioGraphAudioSetting audioGraphSetting)
        {
            var setting = await audioGraphSetting.GetAudioGraphSettingsAsync();
            var graphResult = await AudioGraph.CreateAsync(setting);
            if (graphResult.Status == AudioGraphCreationStatus.Success)
            {
                var outputResult = await graphResult.Graph.CreateDeviceOutputNodeAsync();
                if (outputResult.Status == AudioDeviceNodeCreationStatus.Success)
                    return (graphResult.Graph, outputResult.DeviceOutputNode);
                graphResult.Graph.Dispose();
                if (audioGraphSetting.PreferredSampleRate == 0) throw outputResult.ExtendedError;
            }
            else if (audioGraphSetting.PreferredSampleRate == 0)
            {
                throw graphResult.ExtendedError;
            }

            audioGraphSetting.DisablePreferredSampleRate();
            setting = await audioGraphSetting.GetAudioGraphSettingsAsync();
            graphResult = await AudioGraph.CreateAsync(setting);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw graphResult.ExtendedError;
            var fallbackOutput = await graphResult.Graph.CreateDeviceOutputNodeAsync();
            if (fallbackOutput.Status != AudioDeviceNodeCreationStatus.Success)
                throw fallbackOutput.ExtendedError;
            return (graphResult.Graph, fallbackOutput.DeviceOutputNode);
        }

        private void GraphOnQuantumProcessed(AudioGraph sender, object args)
        {
            if (!_callbackGate.TryEnter()) return;
            _inGraphCallback = true;
            try
            {
                ProcessQuantumEnd();
            }
            finally
            {
                _inGraphCallback = false;
                _callbackGate.Exit();
            }
        }

        private void ProcessQuantumEnd()
        {
            PlaybackTap[] taps;
            lock (_taps) taps = _taps.Count == 0 ? Array.Empty<PlaybackTap>() : _taps.ToArray();
            foreach (var tap in taps)
            {
                try
                {
                    tap.Pull();
                }
                catch
                {
                }
            }
            using var frame = _frameOutputNode.GetFrame();
            if (!EnableFFTProcessing) return;
            try
            {
                FFTProcessor.ProcessFFT(frame);
            }
            catch
            {
                //Ignore
            }
        }

        private void GraphOnQuantumStarted(AudioGraph sender, object args)
        {
            if (!_callbackGate.TryEnter()) return;
            _inGraphCallback = true;
            try
            {
                ProcessQuantumStart(sender);
            }
            finally
            {
                _inGraphCallback = false;
                _callbackGate.Exit();
            }
        }

        private void ProcessQuantumStart(AudioGraph sender)
        {
            var sampleRate = sender.EncodingProperties.SampleRate;
            if (sampleRate == 0) return;
            var step = sender.SamplesPerQuantum / (double)sampleRate;
            List<GainRamp> finished = null;
            lock (_rampLock)
            {
                if (_masterRamp != null)
                {
                    _masterFade = Advance(_masterRamp, step);
                    ApplyMasterGain();
                    if (_masterRamp.Elapsed >= _masterRamp.Duration)
                    {
                        (finished ??= new()).Add(_masterRamp);
                        _masterRamp = null;
                    }
                }

                for (var i = _ramps.Count - 1; i >= 0; i--)
                {
                    var ramp = _ramps[i];
                    var gain = Advance(ramp, step);
                    if (_voiceBuses.TryGetValue(ramp.Source, out var bus)) bus.OutgoingGain = gain;
                    if (ramp.Elapsed < ramp.Duration) continue;
                    _ramps.RemoveAt(i);
                    (finished ??= new()).Add(ramp);
                }
            }

            _quantumIndex++;
            Action[] hooks;
            lock (_quantumHooks) hooks = _quantumHooks.Count == 0 ? Array.Empty<Action>() : _quantumHooks.ToArray();
            foreach (var hook in hooks)
            {
                try
                {
                    hook();
                }
                catch
                {
                }
            }

            if (finished == null) return;
            foreach (var ramp in finished) ramp.Completion.TrySetResult(true);
            ThreadPool.QueueUserWorkItem(_ => RefreshCallbacks());
        }

        private bool CallbacksNeeded()
        {
            if (_enableFftProcessing) return true;
            lock (_taps)
                if (_taps.Count > 0) return true;
            lock (_quantumHooks)
                if (_quantumHooks.Count > 0) return true;
            lock (_rampLock)
                return _masterRamp != null || _ramps.Count > 0;
        }

        private void RefreshCallbacks()
        {
            if (_inGraphCallback)
            {
                ThreadPool.QueueUserWorkItem(_ => RefreshCallbacks());
                return;
            }
            lock (_callbackLock)
            {
                var graph = _defaultPlayer;
                var needed = graph != null && CallbacksNeeded();
                var target = needed ? graph : null;
                if (ReferenceEquals(target, _callbackGraph) && ReferenceEquals(_runningFrameOutput, needed ? _frameOutputNode : null)) return;

                if (_callbackGraph != null)
                {
                    _callbackGraph.QuantumStarted -= GraphOnQuantumStarted;
                    _callbackGraph.QuantumProcessed -= GraphOnQuantumProcessed;
                    _callbackGraph = null;
                }
                _callbackGate.CloseAndDrain();
                var frameOutput = _frameOutputNode;
                try
                {
                    if (needed) frameOutput?.Start();
                    else frameOutput?.Stop();
                }
                catch
                {
                }
                _runningFrameOutput = needed ? frameOutput : null;


                if (target == null) return;
                _callbackGate.Reopen();
                target.QuantumStarted += GraphOnQuantumStarted;
                target.QuantumProcessed += GraphOnQuantumProcessed;
                _callbackGraph = target;
            }
        }


        public long QuantumIndex => Interlocked.Read(ref _quantumIndex);
        public int SamplesPerQuantum => _defaultPlayer?.SamplesPerQuantum ?? 0;

        public event Action OnGraphRebuilt;

        public bool RealtimeCallbacksActive => Volatile.Read(ref _callbackGraph) != null;

        public void AddQuantumHook(Action hook)
        {
            lock (_quantumHooks) _quantumHooks.Add(hook);
            RefreshCallbacks();
        }

        public void RemoveQuantumHook(Action hook)
        {
            lock (_quantumHooks) _quantumHooks.Remove(hook);
            RefreshCallbacks();
        }

        public PlaybackTap AttachTap(IPlaybackSource playbackSource, double seconds = 4)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);
            var properties = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(_defaultPlayer.EncodingProperties.SampleRate, 2, 32);
            properties.Subtype = Windows.Media.MediaProperties.MediaEncodingSubtypes.Float;
            var output = _defaultPlayer.CreateFrameOutputNode(properties);
            node.AddOutgoingConnection(output);
            var tap = new PlaybackTap(output, (int)properties.SampleRate, seconds);
            lock (_taps)
            {
                _taps.Add(tap);
                if (playbackSource is AudioGraphPlaybackSource owner) _tapSources[tap] = owner;
            }
            RefreshCallbacks();
            return tap;
        }

        public void DetachTap(IPlaybackSource playbackSource, PlaybackTap tap)
        {
            if (tap == null) return;
            AudioGraphPlaybackSource owner;
            lock (_taps)
            {
                if (!_taps.Remove(tap)) return;
                _tapSources.Remove(tap, out owner);
            }
            tap.Gate.CloseAndDrain();
            try
            {
                var source = playbackSource as AudioGraphPlaybackSource ?? owner;
                if (source != null && _audioInputNodes.TryGetValue(source, out var node))
                    node.RemoveOutgoingConnection(tap.Node);
            }
            catch
            {
            }
            DisposeInBackground(tap.Node);
            RefreshCallbacks();
        }

        public MixRenderer CreateMixRenderer(IMixRenderSource source)
        {
            ThrowExceptionIfDisposed();
            var properties = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(_defaultPlayer.EncodingProperties.SampleRate, 2, 32);
            properties.Subtype = Windows.Media.MediaProperties.MediaEncodingSubtypes.Float;
            var node = _defaultPlayer.CreateFrameInputNode(properties);
            var bus = _defaultPlayer.CreateSubmixNode();
            bus.AddOutgoingConnection(_outputNode);
            bus.AddOutgoingConnection(_frameOutputNode);
            node.AddOutgoingConnection(bus);
            var renderer = new MixRenderer(node, bus, source);
            lock (_renderers) _renderers.Add(renderer);
            node.Start();
            return renderer;
        }

        public void ReleaseMixRenderer(MixRenderer renderer)
        {
            if (renderer == null) return;
            lock (_renderers) _renderers.Remove(renderer);
            renderer.Detach();
            try
            {
                renderer.Node.Stop();
                renderer.Node.RemoveOutgoingConnection(renderer.Bus);
                renderer.Bus.RemoveOutgoingConnection(_outputNode);
                renderer.Bus.RemoveOutgoingConnection(_frameOutputNode);
            }
            catch
            {
            }
            DisposeInBackground(renderer.Node);
            DisposeInBackground(renderer.Bus);
        }

        private static double Advance(GainRamp ramp, double step)
        {
            ramp.Elapsed = Math.Min(ramp.Duration, ramp.Elapsed + step);
            var t = ramp.Duration <= 0 ? 1 : ramp.Elapsed / ramp.Duration;
            var shaped = ramp.Curve == FadeCurve.EqualPower
                ? (ramp.To >= ramp.From ? Math.Sin(t * Math.PI / 2) : 1 - Math.Cos(t * Math.PI / 2))
                : t;
            return ramp.From + (ramp.To - ramp.From) * shaped;
        }

        private void ApplyMasterGain()
        {
            if (_outputNode == null) return;
            _outputNode.OutgoingGain = _muted ? 0 : _volume * _masterFade;
        }

        private readonly SemaphoreSlim _deviceChangeLock = new(1, 1);

        public async Task ChangePlayerServiceImplementation(IAudioSettings settings)
        {
            ThrowExceptionIfDisposed();
            if (settings is not AudioGraphAudioSetting audioGraphSetting)
                throw new ArgumentException("Setting is not AudioGraphSetting");

            await _deviceChangeLock.WaitAsync();
            try
            {
                if (_defaultPlayer != null && _currentDeviceId == audioGraphSetting.ResolvedDeviceId)
                    return;
                _positionTimer.Stop();
                await RebuildGraphAsync(audioGraphSetting);
            }
            finally
            {
                _deviceChangeLock.Release();
            }
        }

        private async Task RebuildGraphAsync(AudioGraphAudioSetting audioGraphSetting)
        {
            var settings = audioGraphSetting;

            var oldPlayer = _defaultPlayer;
            var oldNodes = _audioInputNodes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var oldGains = _voiceBuses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OutgoingGain);
            CancelAllRamps();

            try
            {
                // 创建新的 AudioGraph
                var (newPlayer, newOutputNode) = await CreateGraphAsync(audioGraphSetting);

                oldPlayer.Stop();
                lock (_callbackLock)
                {
                    if (_callbackGraph != null)
                    {
                        _callbackGraph.QuantumProcessed -= GraphOnQuantumProcessed;
                        _callbackGraph.QuantumStarted -= GraphOnQuantumStarted;
                        _callbackGraph = null;
                    }
                    _runningFrameOutput = null;
                }
                oldPlayer.UnrecoverableErrorOccurred -= GraphOnUnrecoverableErrorOccurred;

                var encodingProperties = newOutputNode.EncodingProperties.Copy();
                encodingProperties.ChannelCount = 1;
                var frameOutputResult = newPlayer.CreateFrameOutputNode(encodingProperties);
                _frameOutputNode = frameOutputResult;

                // 转移所有播放源
                var newNodes = new ConcurrentDictionary<AudioGraphPlaybackSource, MediaSourceAudioInputNode>();
                var newNodesReverse = new ConcurrentDictionary<MediaSourceAudioInputNode, AudioGraphPlaybackSource>();
                var newBuses = new ConcurrentDictionary<AudioGraphPlaybackSource, AudioSubmixNode>();

                foreach (var (source, oldNode) in oldNodes)
                {
                    // 捕获旧节点状态
                    var position = oldNode.Position;
                    var gain = oldGains.TryGetValue(source, out var busGain) ? busGain : 1d;
                    var factor = oldNode.PlaybackSpeedFactor;
                    var effects = oldNode.EffectDefinitions.ToList();

                    // 清理旧节点
                    oldNode.MediaSourceCompleted -= OnMediaSourceCompleted;
                    oldNode.Dispose();
                    source.PlaybackSource?.Reset();

                    // 准备播放源
                    if (source.PlaybackSource == null)
                        await source.CreatePlaybackSource();
                    await source.PlaybackSource?.OpenAsync();

                    // 在新图中创建节点
                    var createResult = await newPlayer.CreateMediaSourceAudioInputNodeAsync(source.PlaybackSource);
                    if (createResult.Status != MediaSourceAudioInputNodeCreationStatus.Success)
                        throw createResult.ExtendedError;
                    var newNode = createResult.Node;

                    // 应用状态
                    newNode.PlaybackSpeedFactor = factor;
                    var bus = newPlayer.CreateSubmixNode();
                    bus.OutgoingGain = gain;
                    bus.AddOutgoingConnection(newOutputNode);
                    bus.AddOutgoingConnection(_frameOutputNode);
                    newNode.AddOutgoingConnection(bus);
                    newNode.MediaSourceCompleted += OnMediaSourceCompleted;

                    // 应用效果
                    foreach (var effect in effects)
                    {
                        newNode.EffectDefinitions.Add(effect);
                    }

                    // 恢复位置并开始
                    await Task.Delay(250);
                    newNode.Seek(position);
                    newNode.Start();

                    newNodes[source] = newNode;
                    newNodesReverse[newNode] = source;
                    newBuses[source] = bus;
                }

                // 替换为新图
                _defaultPlayer = newPlayer;
                newPlayer.UnrecoverableErrorOccurred += GraphOnUnrecoverableErrorOccurred;
                EnableFFTProcessing = settings.EnableFFTProcessing;
                _outputNode = newOutputNode;
                ApplyMasterGain();
                _audioInputNodes.Clear();
                foreach (var kvp in newNodes) _audioInputNodes.TryAdd(kvp.Key, kvp.Value);
                _audioInputNodesReverseDictionary.Clear();
                foreach (var kvp in newNodesReverse) _audioInputNodesReverseDictionary.TryAdd(kvp.Key, kvp.Value);
                _voiceBuses.Clear();
                foreach (var kvp in newBuses) _voiceBuses.TryAdd(kvp.Key, kvp.Value);

                _currentDeviceId = audioGraphSetting.ResolvedDeviceId;
                lock (_taps) _taps.Clear();
                lock (_renderers) _renderers.Clear();
                OnGraphRebuilt?.Invoke();
                if (GlobalPlaybackStatus == PlaybackStatus.Playing) newPlayer.Start();
            }
            finally
            {
                oldPlayer.Dispose();
                _positionTimer.Start();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposedValue) return;

            if (disposing)
            {
                _positionTimer?.Stop();
                _positionTimer?.Dispose();
            }

            CancelAllRamps();

            // 清理所有播放源
            foreach (var (source, item) in _audioInputNodes)
            {
                if (_voiceBuses.TryRemove(source, out var bus))
                {
                    item.RemoveOutgoingConnection(bus);
                    bus.Dispose();
                }
                item.Dispose();
            }

            _defaultPlayer?.Dispose();
            _disposedValue = true;
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~AudioGraphPlayer()
        {
            Dispose(disposing: false);
        }
        #endregion

        #region Playback Control Methods
        public void PlayAll() => PlayAll(TimeSpan.Zero);

        public void PlayAll(TimeSpan fadeIn)
        {
            ThrowExceptionIfDisposed();
            if (_defaultPlayer == null || ConnectedPlaybackSourceCount == 0) return;
            var wasPlaying = GlobalPlaybackStatus == PlaybackStatus.Playing;
            Interlocked.Increment(ref _transportVersion);
            if (fadeIn > TimeSpan.Zero)
            {
                _ = RampMasterAsync(wasPlaying ? _masterFade : 0, 1, fadeIn);
            }
            else
            {
                CancelMasterRamp(1);
            }
            if (wasPlaying) return;

            _defaultPlayer.Start();
            GlobalPlaybackStatus = PlaybackStatus.Playing;
            OnGlobalPlaybackStatusChanged?.Invoke(PlaybackStatus.Playing);
            SMTCManager?.OnPlayAll();
        }

        public void PauseAll()
        {
            ThrowExceptionIfDisposed();
            if (_defaultPlayer == null || GlobalPlaybackStatus == PlaybackStatus.Paused) return;

            Interlocked.Increment(ref _transportVersion);
            _defaultPlayer.Stop();
            CancelMasterRamp(1);
            GlobalPlaybackStatus = PlaybackStatus.Paused;
            SMTCManager?.OnPauseAll();
            OnGlobalPlaybackStatusChanged?.Invoke(PlaybackStatus.Paused);
        }

        public async Task PauseAllAsync(TimeSpan fadeOut)
        {
            ThrowExceptionIfDisposed();
            if (_defaultPlayer == null || GlobalPlaybackStatus != PlaybackStatus.Playing) return;
            if (fadeOut <= TimeSpan.Zero)
            {
                PauseAll();
                return;
            }

            var version = Interlocked.Increment(ref _transportVersion);
            var completed = await RampMasterAsync(_masterFade, 0, fadeOut);
            if (!completed || version != Volatile.Read(ref _transportVersion)) return;
            PauseAll();
        }

        public void PlayPlaybackSource(IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);

            node.Start();
            if (playbackSource is AudioGraphPlaybackSource source)
            {
                source.PlaybackStatus = PlaybackStatus.Playing;
                OnPlaybackSourceStatusChanged?.Invoke(playbackSource, PlaybackStatus.Playing);
            }
        }

        public void PausePlaybackSource(IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);

            node.Stop();
            if (playbackSource is AudioGraphPlaybackSource source)
            {
                source.PlaybackStatus = PlaybackStatus.Paused;
                OnPlaybackSourceStatusChanged?.Invoke(playbackSource, PlaybackStatus.Paused);
            }
        }

        public void SeekPlaybackSource(TimeSpan target, IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);
            var source = playbackSource as AudioGraphPlaybackSource;
            // 确保不超过音频源时长
            if (source?.PlaybackSource?.Duration != null)
            {
                var value = Math.Min(target.TotalMilliseconds, source.PlaybackSource.Duration.Value.TotalMilliseconds);
                node.Seek(TimeSpan.FromMilliseconds(value));
            }
            else
            {
                node.Seek(target);
            }
        }
        #endregion

        #region Volume and Speed Control
        public void SetOutputVolume(double volume)
        {
            ThrowExceptionIfDisposed();
            if (_outputNode != null)
            {
                _volume = volume;
                ApplyMasterGain();
            }
        }

        public void SetPlaybackSourceOutputVolume(double volume, IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var bus = GetVoiceBusOrThrow(playbackSource);
            CancelRamp((AudioGraphPlaybackSource)playbackSource);
            bus.OutgoingGain = volume;
        }

        public double GetPlaybackSourceOutputVolume(IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            return GetVoiceBusOrThrow(playbackSource).OutgoingGain;
        }

        public Task<bool> RampPlaybackSourceVolumeAsync(IPlaybackSource playbackSource, double target, TimeSpan duration, FadeCurve curve = FadeCurve.EqualPower)
        {
            ThrowExceptionIfDisposed();
            var bus = GetVoiceBusOrThrow(playbackSource);
            var source = (AudioGraphPlaybackSource)playbackSource;
            var ramp = new GainRamp
            {
                Source = source,
                From = bus.OutgoingGain,
                To = target,
                Duration = Math.Max(0, duration.TotalSeconds),
                Curve = curve,
                Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            lock (_rampLock)
            {
                for (var i = _ramps.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(_ramps[i].Source, source)) continue;
                    _ramps[i].Completion.TrySetResult(false);
                    _ramps.RemoveAt(i);
                }
                if (ramp.Duration <= 0)
                {
                    bus.OutgoingGain = target;
                    ramp.Completion.TrySetResult(true);
                    return ramp.Completion.Task;
                }
                _ramps.Add(ramp);
            }
            RefreshCallbacks();
            return ramp.Completion.Task;
        }

        public bool IsPlaybackSourceVolumeRamping(IPlaybackSource playbackSource)
        {
            lock (_rampLock)
            {
                return _ramps.Any(r => ReferenceEquals(r.Source, playbackSource));
            }
        }

        private Task<bool> RampMasterAsync(double from, double to, TimeSpan duration)
        {
            var ramp = new GainRamp
            {
                From = from,
                To = to,
                Duration = Math.Max(0, duration.TotalSeconds),
                Curve = FadeCurve.EqualPower,
                Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            lock (_rampLock)
            {
                _masterRamp?.Completion.TrySetResult(false);
                _masterFade = from;
                ApplyMasterGain();
                _masterRamp = ramp;
            }
            RefreshCallbacks();
            return ramp.Completion.Task;
        }

        private void CancelMasterRamp(double value)
        {
            lock (_rampLock)
            {
                _masterRamp?.Completion.TrySetResult(false);
                _masterRamp = null;
                _masterFade = value;
                ApplyMasterGain();
            }
            RefreshCallbacks();
        }

        private void CancelRamp(AudioGraphPlaybackSource source)
        {
            var removed = false;
            lock (_rampLock)
            {
                for (var i = _ramps.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(_ramps[i].Source, source)) continue;
                    _ramps[i].Completion.TrySetResult(false);
                    _ramps.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) RefreshCallbacks();
        }

        private void CancelAllRamps()
        {
            lock (_rampLock)
            {
                foreach (var ramp in _ramps) ramp.Completion.TrySetResult(false);
                _ramps.Clear();
                _masterRamp?.Completion.TrySetResult(false);
                _masterRamp = null;
                _masterFade = 1;
            }
            RefreshCallbacks();
        }

        public void SetPlaybackSourceSpeed(double speed, IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);
            node.PlaybackSpeedFactor = speed;
        }

        public double GetPlaybackSourceSpeed(IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);
            return node.PlaybackSpeedFactor;
        }
        #endregion

        #region Playback Source Management
        public async Task ConnectPlaybackSourceAsync(IPlaybackSource playbackSource, PlaybackOptions options = null)
        {
            ThrowExceptionIfDisposed();
            options ??= new PlaybackOptions();

            // 验证播放源类型
            if (playbackSource is not AudioGraphPlaybackSource source)
                throw new ArgumentException("PlaybackSource is not AudioGraphPlaybackSource.");

            // 检查是否已连接
            if (_audioInputNodes.ContainsKey(source))
                throw new ArgumentException("PlaybackSource has been connected to the player.");

            if (_defaultPlayer == null) return;

            // 确保播放源已创建
            if (source.PlaybackSource == null)
                await playbackSource.CreatePlaybackSource();

            // 创建音频输入节点
            var nodeResult = await _defaultPlayer.CreateMediaSourceAudioInputNodeAsync(source.PlaybackSource);
            if (nodeResult.Status != MediaSourceAudioInputNodeCreationStatus.Success)
                throw nodeResult.ExtendedError;

            var node = nodeResult.Node;

            // 配置节点
            var fadeIn = options.AutoPlay && options.FadeIn > TimeSpan.Zero;
            var bus = _defaultPlayer.CreateSubmixNode();
            bus.OutgoingGain = fadeIn ? 0 : options.Volume;
            bus.AddOutgoingConnection(_outputNode);
            bus.AddOutgoingConnection(_frameOutputNode);
            node.AddOutgoingConnection(bus);
            node.MediaSourceCompleted += OnMediaSourceCompleted;

            // 注册节点
            _voiceBuses.TryAdd(source, bus);
            _audioInputNodes.TryAdd(source, node);
            _audioInputNodesReverseDictionary.TryAdd(node, source);

            // 设置为主播放源
            if (_audioInputNodes.Count == 1 || options.SetAsPrimarySource)
                PrimaryPlaybackSource = playbackSource;

            // 根据选项设置播放状态
            if (!options.AutoPlay)
            {
                node.Stop();
                source.PlaybackStatus = PlaybackStatus.Paused;
            }
            else
            {
                if (fadeIn) _ = RampPlaybackSourceVolumeAsync(source, options.Volume, options.FadeIn);
                if (GlobalPlaybackStatus is not PlaybackStatus.Playing)
                {
                    PlayAll();
                }
            }
        }

        public int DisconnectAllExcept(IPlaybackSource keep)
        {
            var removed = 0;
            foreach (var source in _audioInputNodes.Keys)
            {
                if (ReferenceEquals(source, keep)) continue;
                try
                {
                    DisconnectPlaybackSource(source);
                    removed++;
                }
                catch
                {
                }
            }
            return removed;
        }

        public void DisconnectPlaybackSource(IPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            if (playbackSource is not AudioGraphPlaybackSource source)
                throw new ArgumentException("PlaybackSource is not AudioGraphPlaybackSource.");

            if (!_audioInputNodes.TryGetValue(source, out MediaSourceAudioInputNode node)) return;
            node.MediaSourceCompleted -= OnMediaSourceCompleted;

            if (PrimaryPlaybackSource == source)
                PrimaryPlaybackSource = null;

            CancelRamp(source);
            PlaybackTap[] orphanTaps;
            lock (_taps) orphanTaps = _tapSources.Where(pair => ReferenceEquals(pair.Value, source)).Select(pair => pair.Key).ToArray();
            foreach (var tap in orphanTaps) DetachTap(source, tap);
            ReleaseVoice(source, node);

            _audioInputNodes.TryRemove(source, out _);
            _audioInputNodesReverseDictionary.TryRemove(node, out _);
        }

        private void ReleaseVoice(AudioGraphPlaybackSource source, MediaSourceAudioInputNode node)
        {
            if (_voiceBuses.TryRemove(source, out var bus))
            {
                node.RemoveOutgoingConnection(bus);
                bus.RemoveOutgoingConnection(_outputNode);
                bus.RemoveOutgoingConnection(_frameOutputNode);
                DisposeInBackground(bus);
            }
            DisposeInBackground(node);
        }

        private static void DisposeInBackground(IDisposable node)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    node.Dispose();
                }
                catch
                {
                }
            });
        }

        public void RemoveAllPlaybackSource()
        {
            ThrowExceptionIfDisposed();
            foreach (var source in _audioInputNodes)
            {
                source.Value.MediaSourceCompleted -= OnMediaSourceCompleted;
                CancelRamp(source.Key);
                ReleaseVoice(source.Key, source.Value);
            }
            _audioInputNodes.Clear();
            _audioInputNodesReverseDictionary.Clear();
            _primaryPlaybackSource = null;
        }

        public List<AudioGraphPlaybackSource> GetConnectedPlaybackSource()
        {
            ThrowExceptionIfDisposed();
            return _audioInputNodes.Keys.ToList();
        }

        public MediaSourceAudioInputNode GetAudioInputNode(AudioGraphPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            return _audioInputNodes[playbackSource];
        }
        #endregion

        #region Effect Management
        public void AddEffectToPlaybackSource(IAudioEffectDefinition definition, AudioGraphPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);
            if (!node.EffectDefinitions.Contains(definition)) node.EffectDefinitions.Add(definition);
            node.EnableEffectsByDefinition(definition);
        }

        public void RemoveEffectFromPlaybackSource(IAudioEffectDefinition definition, AudioGraphPlaybackSource playbackSource)
        {
            ThrowExceptionIfDisposed();
            var node = GetAudioInputNodeOrThrow(playbackSource);
            node.DisableEffectsByDefinition(definition);
            node.EffectDefinitions.Remove(definition);
        }
        #endregion

        #region Event Handlers
        private void PositionTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (PrimaryPlaybackSource is null || GlobalPlaybackStatus is PlaybackStatus.Paused) return;

            var source = PrimaryPlaybackSource as AudioGraphPlaybackSource;
            if (!_audioInputNodes.TryGetValue(source, out var track)) return;

            var position = track?.Position;
            if (position == null || position == _lastPosition) return;

            _lastPosition = position.Value;
            OnPositionChanged?.Invoke(position.Value);

            // 更新系统媒体传输控制
            var positionProperties = new SystemMediaTransportControlsTimelineProperties
            {
                Position = position.Value,
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = track.Duration,
                MaxSeekTime = track.Duration
            };
            SMTCManager?.OnPositionChange(positionProperties);
        }

        private void OnMediaSourceCompleted(MediaSourceAudioInputNode sender, object args)
        {
            if (_audioInputNodesReverseDictionary.TryGetValue(sender, out var playbackSource))
            {
                OnTrackReachesEnd?.Invoke(playbackSource);
            }
        }
        #endregion

        #region Helper Methods
        private MediaSourceAudioInputNode GetAudioInputNodeOrThrow(IPlaybackSource playbackSource)
        {
            if (playbackSource is not AudioGraphPlaybackSource source)
                throw new ArgumentException("PlaybackSource is not AudioGraphPlaybackSource.");

            if (!_audioInputNodes.TryGetValue(source, out var node))
                throw new ArgumentException("PlaybackSource haven't connected to the player.");

            return node;
        }

        private AudioSubmixNode GetVoiceBusOrThrow(IPlaybackSource playbackSource)
        {
            if (playbackSource is not AudioGraphPlaybackSource source)
                throw new ArgumentException("PlaybackSource is not AudioGraphPlaybackSource.");

            if (!_voiceBuses.TryGetValue(source, out var bus))
                throw new ArgumentException("PlaybackSource haven't connected to the player.");

            return bus;
        }

        private void ThrowExceptionIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposedValue, this);
        }
        #endregion
    }
}
