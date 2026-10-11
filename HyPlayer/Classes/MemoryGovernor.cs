#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.Services.Playback;
using HyPlayer.Services.Playback.Models;
using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace HyPlayer.Classes;

public static class MemoryGovernor
{
    public const int AutoLimit = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern unsafe int K32GetProcessMemoryInfo(nint process, ProcessMemoryCounters* counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern int K32EmptyWorkingSet(nint process);

    [DllImport("kernel32.dll")]
    private static extern unsafe int GlobalMemoryStatusEx(MemoryStatus* status);

    private const ulong MB = 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);
    private const ulong RegrowthBeforeRetry = 256 * MB;
    private const ulong IneffectiveReclaim = 64 * MB;

    private static Timer? _pollTimer;
    private static CancellationTokenSource? _inactiveCts;
    private static Window? _window;
    private static int _reclaiming;
    private static DateTime _lastReclaim = DateTime.MinValue;
    private static TimeSpan _cooldown = BaseCooldown;
    private static ulong _workingSetAfterReclaim;
    private static bool _windowInactive;
    private static bool _appInBackground;

    public static unsafe ulong WorkingSetBytes
    {
        get
        {
            var counters = new ProcessMemoryCounters { Size = (uint)sizeof(ProcessMemoryCounters) };
            return K32GetProcessMemoryInfo(GetCurrentProcess(), &counters, counters.Size) != 0 ? counters.WorkingSetSize : 0;
        }
    }

    private static bool IsInactive => _windowInactive || _appInBackground;

    public static void Attach(Window window)
    {
        if (ReferenceEquals(_window, window)) return;
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = window;
        window.Activated += OnWindowActivated;
        _pollTimer ??= new Timer(_ => Evaluate(), null, PollInterval, PollInterval);
    }

    public static void OnEnteredBackground()
    {
        _appInBackground = true;
        RestartInactiveTimer();
    }

    public static void OnLeavingBackground()
    {
        _appInBackground = false;
        if (!_windowInactive) CancelInactiveTimer();
    }

    private static void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        _windowInactive = e.WindowActivationState == CoreWindowActivationState.Deactivated;
        if (_windowInactive) RestartInactiveTimer();
        else if (!_appInBackground) CancelInactiveTimer();
    }

    private static void CancelInactiveTimer()
    {
        Interlocked.Exchange(ref _inactiveCts, null)?.Cancel();
    }

    private static void RestartInactiveTimer()
    {
        var seconds = Ioc.Default.GetRequiredService<Setting>().BackgroundReleaseSeconds;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _inactiveCts, cts)?.Cancel();
        if (seconds <= 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                while (IsBusy()) await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (IsInactive && CooldownElapsed()) Reclaim();
        });
    }

    private static bool CooldownElapsed() => DateTime.UtcNow - _lastReclaim >= _cooldown;

    private static bool IsBusy()
    {
        try
        {
            if (Ioc.Default.GetRequiredService<ModelManager>().IsBusy || MemoryReclaimer.RealtimeAudioBusy()) return true;
            return Ioc.Default.GetRequiredService<PlaybackStateService>().IsTransitioning;
        }
        catch
        {
            return false;
        }
    }

    private static void Evaluate()
    {
        try
        {
            var limitMb = Ioc.Default.GetRequiredService<Setting>().MemoryLimitMB;
            if (limitMb == 0 || !CooldownElapsed() || IsBusy()) return;
            var workingSet = WorkingSetBytes;
            if (workingSet < _workingSetAfterReclaim + RegrowthBeforeRetry) return;
            var threshold = limitMb > 0 ? (ulong)limitMb * MB : AutoThreshold();
            if (workingSet > threshold) Reclaim();
        }
        catch
        {
        }
    }

    private static unsafe ulong AutoThreshold()
    {
        var status = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
        var known = GlobalMemoryStatusEx(&status) != 0 && status.TotalPhys > 0;
        var systemTight = MemoryManager.AppMemoryUsageLevel >= AppMemoryUsageLevel.High ||
                          known && (status.AvailPhys < status.TotalPhys / 8 || status.AvailPhys < 1536 * MB);
        if (systemTight) return 384 * MB;
        if (IsInactive) return 768 * MB;
        var total = known ? status.TotalPhys : 8192 * MB;
        return Math.Max(2048 * MB, total / 5);
    }

    public static ulong Reclaim()
    {
        if (Interlocked.Exchange(ref _reclaiming, 1) == 1) return 0;
        var before = WorkingSetBytes;
        ulong freed = 0;
        try
        {
            try
            {
                Ioc.Default.GetRequiredService<ModelManager>().ReleaseIdleSessions();
            }
            catch
            {
            }
            if (Ioc.Default.GetRequiredService<PlaybackStateService>().IsPlaying)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            }
            else
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            K32EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
        }
        finally
        {
            var after = WorkingSetBytes;
            freed = before > after ? before - after : 0;
            _cooldown = freed < IneffectiveReclaim ? TimeSpan.FromTicks(Math.Min(_cooldown.Ticks * 2, MaxCooldown.Ticks)) : BaseCooldown;
            _workingSetAfterReclaim = after;
            _lastReclaim = DateTime.UtcNow;
            Volatile.Write(ref _reclaiming, 0);
        }
        return freed;
    }

    public static Task<ulong> ReclaimAsync() => Task.Run(Reclaim);
}
