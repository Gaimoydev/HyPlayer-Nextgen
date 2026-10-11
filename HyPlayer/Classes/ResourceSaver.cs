#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.Classes.Settings;
using HyPlayer.Services.Abstractions;
using HyPlayer.Services.Playback.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.System.Power;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace HyPlayer.Classes;

internal sealed class SettingChange
{
    public required string Key { get; init; }
    public required Func<string> Read { get; init; }
    public required Action<string> Write { get; init; }
    public required string Target { get; init; }
    public bool Runtime { get; init; }
}

internal sealed class SaverOption
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required SettingChange[] Changes { get; init; }
}

public static class ResourceSaver
{
    private const string NetworkBackupKey = "ResourceSaverBackupNetwork";
    private const string PowerBackupKey = "ResourceSaverBackupPower";

    private static volatile bool _relaxedNetwork;
    private static bool _attached;
    private static bool _networkActive;
    private static bool _powerActive;
    private static bool _networkPrompted;
    private static bool _powerPrompted;
    private static int _probing;
    private static readonly SemaphoreSlim DialogGate = new(1, 1);
    private static readonly List<(DateTime Time, bool Slow)> Requests = new();
    private static readonly object SlowLock = new();
    private static DateTime _lastSlow = DateTime.MinValue;
    internal static readonly HttpRequestOptionsKey<bool> ProbeKey = new("HyPlayerProbe");

    public static bool RelaxedNetwork => _relaxedNetwork;

    private static Setting Setting => Ioc.Default.GetRequiredService<Setting>();
    private static CoreDispatcher Dispatcher => CoreApplication.MainView.Dispatcher;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        PowerManager.EnergySaverStatusChanged += (_, _) => OnPowerChanged();
        PowerManager.PowerSupplyStatusChanged += (_, _) => OnPowerChanged();
        PowerManager.BatteryStatusChanged += (_, _) => OnPowerChanged();

        ReapplyRuntime(NetworkBackupKey);
        ReapplyRuntime(PowerBackupKey);
        _networkActive = HasBackup(NetworkBackupKey);
        _powerActive = HasBackup(PowerBackupKey);
        if (_networkActive) StartProbing();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            OnPowerChanged();
        });
    }

    public static void ReportLoad(HyPlayItem item, TimeSpan elapsed)
    {
        if (item.ProviderId is not ("nca" or "nst")) return;
        if (elapsed < TimeSpan.FromSeconds(8)) return;
        lock (SlowLock) _lastSlow = DateTime.UtcNow;
        SuspectWeakNetwork();
    }

    public static void ReportTimeout()
    {
        lock (SlowLock) _lastSlow = DateTime.UtcNow;
        SuspectWeakNetwork();
    }

    internal static void ReportRequest(TimeSpan elapsed, bool failed)
    {
        var slow = failed || elapsed > TimeSpan.FromSeconds(3);
        bool trigger;
        lock (SlowLock)
        {
            var now = DateTime.UtcNow;
            Requests.RemoveAll(r => now - r.Time > TimeSpan.FromSeconds(90));
            Requests.Add((now, slow));
            if (slow) _lastSlow = now;
            var slowCount = Requests.Count(r => r.Slow);
            trigger = slowCount >= 3 && slowCount * 2 >= Requests.Count;
        }
        if (trigger) SuspectWeakNetwork();
    }

    private static void SuspectWeakNetwork()
    {
        if (_networkActive || _networkPrompted) return;
        _ = RunOnUiAsync(() => PromptNetworkAsync());
    }

    private static async Task PromptNetworkAsync()
    {
        if (_networkActive || _networkPrompted) return;
        _networkPrompted = true;
        if (!Setting.promptWeakNetwork)
        {
            StartProbing();
            return;
        }

        var options = BuildNetworkOptions();
        var result = await ShowDialogAsync(
            "网络状况较差",
            "检测到当前网络较慢，加载可能较为卡顿。可以临时做以下调整，网络恢复后会自动还原：",
            options,
            disable => Setting.promptWeakNetwork = !disable);
        if (result is null || result.Count == 0)
        {
            StartProbing();
            return;
        }
        Apply(NetworkBackupKey, result);
        _networkActive = true;
        StartProbing();
    }

    private static List<SaverOption> BuildNetworkOptions()
    {
        var setting = Setting;
        var options = new List<SaverOption>();
        if (setting.audioRate != "standard")
            options.Add(new SaverOption
            {
                Title = "将在线播放音质降为 标准 (128k)",
                Changes = [Change("audioRate", () => setting.audioRate, v => setting.audioRate = v, "standard")]
            });
        if (!setting.noImage)
            options.Add(new SaverOption
            {
                Title = "不加载封面图片",
                Description = "新打开的页面生效",
                Changes = [BoolChange("noImage", () => setting.noImage, v => setting.noImage = v, true)]
            });
        if (setting.greedlyLoadPlayContainerItems)
            options.Add(new SaverOption
            {
                Title = "关闭歌单贪婪加载",
                Changes = [BoolChange("greedlyLoadPlayContainerItems", () => setting.greedlyLoadPlayContainerItems, v => setting.greedlyLoadPlayContainerItems = v, false)]
            });
        if (setting.enableAmllTtmlDb)
            options.Add(new SaverOption
            {
                Title = "暂停从 AMLL 歌词库获取逐字歌词",
                Changes = [BoolChange("enableAmllTtmlDb", () => setting.enableAmllTtmlDb, v => setting.enableAmllTtmlDb = v, false)]
            });
        options.Add(new SaverOption
        {
            Title = "提高请求重试次数与超时时间",
            Description = "加载超时从 25 秒放宽到 60 秒，获取播放链接最多重试 6 次",
            Changes = [RuntimeChange("relaxedNetwork", () => _relaxedNetwork, v => _relaxedNetwork = v, true)]
        });
        return options;
    }

    private static void OnPowerChanged()
    {
        _ = RunOnUiAsync(async () =>
        {
            var saving = IsPowerSaving();
            if (saving)
            {
                if (!_powerActive && !_powerPrompted) await PromptPowerAsync();
                return;
            }
            _powerPrompted = false;
            if (_powerActive)
            {
                _powerActive = false;
                if (Restore(PowerBackupKey))
                    Ioc.Default.GetRequiredService<INotificationService>().ShowMessage("已恢复电源", "已还原节能模式下调整的设置");
            }
        });
    }

    private static bool IsPowerSaving()
    {
        try
        {
            if (PowerManager.EnergySaverStatus == EnergySaverStatus.On) return true;
            return PowerManager.BatteryStatus == BatteryStatus.Discharging && PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;
        }
        catch
        {
            return false;
        }
    }

    private static async Task PromptPowerAsync()
    {
        _powerPrompted = true;
        if (!Setting.promptPowerSaving) return;
        var options = BuildPowerOptions();
        if (options.Count == 0) return;
        var reason = PowerManager.EnergySaverStatus == EnergySaverStatus.On ? "Windows 已开启节能模式" : "设备正在使用电池供电";
        var result = await ShowDialogAsync(
            "节能模式",
            $"{reason}。可以临时关闭以下较为耗电的功能，插入电源或关闭节能后会自动还原：",
            options,
            disable => Setting.promptPowerSaving = !disable);
        if (result is null || result.Count == 0) return;
        if (!IsPowerSaving()) return;
        Apply(PowerBackupKey, result);
        _powerActive = true;
    }

    private static List<SaverOption> BuildPowerOptions()
    {
        var setting = Setting;
        var options = new List<SaverOption>();
        var models = Ioc.Default.GetRequiredService<ModelManager>();
        if (models.IsUsable(ModelCatalog.BeatThisId) || models.IsUsable(ModelCatalog.VocalId) || models.IsUsable(ModelCatalog.SKeyId))
            options.Add(new SaverOption
            {
                Title = "暂停自动混音的模型分析",
                Description = "改用轻量的算法分析节拍，已分析过的歌曲不受影响",
                Changes = [RuntimeChange("modelsSuspended", () => ModelManager.Suspended, v => ModelManager.Suspended = v, true)]
            });
        if (setting.EnableFFT)
            options.Add(new SaverOption
            {
                Title = "关闭频谱可视化",
                Changes = [BoolChange("EnableFFT", () => setting.EnableFFT, v => setting.EnableFFT = v, false)]
            });
        if (setting.LyricRendererFPS > 30)
            options.Add(new SaverOption
            {
                Title = "歌词渲染帧率降至 30 FPS",
                Changes = [Change("LyricRendererFPS", () => setting.LyricRendererFPS.ToString(), v => setting.LyricRendererFPS = int.Parse(v), "30")]
            });
        if (setting.lyricRenderBlur)
            options.Add(new SaverOption
            {
                Title = "关闭歌词模糊效果",
                Changes = [BoolChange("lyricRenderBlur", () => setting.lyricRenderBlur, v => setting.lyricRenderBlur = v, false)]
            });
        var breath = new List<SettingChange>();
        if (setting.playbarBackgroundBreath)
            breath.Add(BoolChange("playbarBackgroundBreath", () => setting.playbarBackgroundBreath, v => setting.playbarBackgroundBreath = v, false));
        if (setting.expandAlbumBreath)
            breath.Add(BoolChange("expandAlbumBreath", () => setting.expandAlbumBreath, v => setting.expandAlbumBreath = v, false));
        if (breath.Count > 0)
            options.Add(new SaverOption { Title = "关闭封面与播放栏的呼吸动画", Changes = [.. breath] });
        if (setting.animationAdaptBPM)
            options.Add(new SaverOption
            {
                Title = "关闭随节拍变化的动画",
                Changes = [BoolChange("animationAdaptBPM", () => setting.animationAdaptBPM, v => setting.animationAdaptBPM = v, false)]
            });
        if (setting.greedlyLoadPlayContainerItems)
            options.Add(new SaverOption
            {
                Title = "关闭歌单贪婪加载",
                Changes = [BoolChange("greedlyLoadPlayContainerItems", () => setting.greedlyLoadPlayContainerItems, v => setting.greedlyLoadPlayContainerItems = v, false)]
            });
        return options;
    }

    private static void StartProbing()
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            var good = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    var latency = await ProbeAsync();
                    bool quiet;
                    lock (SlowLock) quiet = DateTime.UtcNow - _lastSlow > TimeSpan.FromSeconds(90);
                    good = quiet && latency is { } ms && ms < TimeSpan.FromSeconds(1) ? good + 1 : 0;
                    if (good < 3) continue;
                    await RunOnUiAsync(() =>
                    {
                        _networkPrompted = false;
                        lock (SlowLock) Requests.Clear();
                        if (!_networkActive) return Task.CompletedTask;
                        _networkActive = false;
                        if (Restore(NetworkBackupKey))
                            Ioc.Default.GetRequiredService<INotificationService>().ShowMessage("网络已恢复", "已还原弱网时调整的设置");
                        return Task.CompletedTask;
                    });
                    return;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _probing, 0);
            }
        });
    }

    private static async Task<TimeSpan?> ProbeAsync()
    {
        try
        {
            var client = Ioc.Default.GetRequiredService<HttpClient>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://interface.music.163.com/");
            request.Options.Set(ProbeKey, true);
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return watch.Elapsed;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<SaverOption>?> ShowDialogAsync(string title, string message, List<SaverOption> options, Action<bool> setDontAsk)
    {
        if (options.Count == 0) return null;
        await DialogGate.WaitAsync();
        try
        {
            var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            var boxes = new List<(CheckBox Box, SaverOption Option)>();
            foreach (var option in options)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = option.Title, TextWrapping = TextWrapping.Wrap });
                if (option.Description is { } description)
                    content.Children.Add(new TextBlock
                    {
                        Text = description,
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap
                    });
                var box = new CheckBox { Content = content, IsChecked = true };
                boxes.Add((box, option));
                panel.Children.Add(box);
            }
            var dontAsk = new CheckBox { Content = "下次不再弹出此提示", IsChecked = false, Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(dontAsk);
            panel.Children.Add(new TextBlock
            {
                Text = "可以在 设置 → 播放设置 → 弱网与节能 中重新开启",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap
            });

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "应用",
                CloseButtonText = "保持现状",
                DefaultButton = ContentDialogButton.Primary
            };

            ContentDialogResult result;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    result = await dialog.ShowAsync();
                    break;
                }
                catch when (attempt < 30)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            if (dontAsk.IsChecked == true) setDontAsk(true);
            if (result != ContentDialogResult.Primary) return [];
            return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Option).ToList();
        }
        catch
        {
            return null;
        }
        finally
        {
            DialogGate.Release();
        }
    }

    private static void Apply(string backupKey, List<SaverOption> options)
    {
        var entries = new List<string>();
        foreach (var change in options.SelectMany(o => o.Changes))
        {
            string original;
            try
            {
                original = change.Read();
                if (original == change.Target) continue;
                change.Write(change.Target);
            }
            catch
            {
                continue;
            }
            entries.Add($"{change.Key}={Uri.EscapeDataString(original)}|{Uri.EscapeDataString(change.Target)}");
        }
        SettingsBase.SetSettings(backupKey, string.Join(";", entries));
    }

    private static bool Restore(string backupKey)
    {
        var entries = Parse(SettingsBase.GetSettings(backupKey, string.Empty));
        SettingsBase.SetSettings(backupKey, string.Empty);
        if (entries.Count == 0) return false;
        var catalog = Catalog();
        foreach (var (key, original, target) in entries)
        {
            if (!catalog.TryGetValue(key, out var change)) continue;
            try
            {
                if (change.Read() == target) change.Write(original);
            }
            catch
            {
            }
        }
        return true;
    }

    private static void ReapplyRuntime(string backupKey)
    {
        var catalog = Catalog();
        foreach (var (key, _, target) in Parse(SettingsBase.GetSettings(backupKey, string.Empty)))
        {
            if (catalog.TryGetValue(key, out var change) && change.Runtime)
                change.Write(target);
        }
    }

    private static bool HasBackup(string backupKey) => !string.IsNullOrEmpty(SettingsBase.GetSettings(backupKey, string.Empty));

    private static List<(string Key, string Original, string Target)> Parse(string text)
    {
        var result = new List<(string, string, string)>();
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            var bar = entry.LastIndexOf('|');
            if (eq <= 0 || bar <= eq) continue;
            result.Add((entry[..eq], Uri.UnescapeDataString(entry[(eq + 1)..bar]), Uri.UnescapeDataString(entry[(bar + 1)..])));
        }
        return result;
    }

    private static Dictionary<string, SettingChange> Catalog()
    {
        var all = BuildNetworkOptionsForCatalog().Concat(BuildPowerOptionsForCatalog());
        var map = new Dictionary<string, SettingChange>();
        foreach (var change in all) map[change.Key] = change;
        return map;
    }

    private static IEnumerable<SettingChange> BuildNetworkOptionsForCatalog()
    {
        var setting = Setting;
        yield return Change("audioRate", () => setting.audioRate, v => setting.audioRate = v, "standard");
        yield return BoolChange("noImage", () => setting.noImage, v => setting.noImage = v, true);
        yield return BoolChange("greedlyLoadPlayContainerItems", () => setting.greedlyLoadPlayContainerItems, v => setting.greedlyLoadPlayContainerItems = v, false);
        yield return BoolChange("enableAmllTtmlDb", () => setting.enableAmllTtmlDb, v => setting.enableAmllTtmlDb = v, false);
        yield return RuntimeChange("relaxedNetwork", () => _relaxedNetwork, v => _relaxedNetwork = v, true);
    }

    private static IEnumerable<SettingChange> BuildPowerOptionsForCatalog()
    {
        var setting = Setting;
        yield return RuntimeChange("modelsSuspended", () => ModelManager.Suspended, v => ModelManager.Suspended = v, true);
        yield return BoolChange("EnableFFT", () => setting.EnableFFT, v => setting.EnableFFT = v, false);
        yield return Change("LyricRendererFPS", () => setting.LyricRendererFPS.ToString(), v => setting.LyricRendererFPS = int.Parse(v), "30");
        yield return BoolChange("lyricRenderBlur", () => setting.lyricRenderBlur, v => setting.lyricRenderBlur = v, false);
        yield return BoolChange("playbarBackgroundBreath", () => setting.playbarBackgroundBreath, v => setting.playbarBackgroundBreath = v, false);
        yield return BoolChange("expandAlbumBreath", () => setting.expandAlbumBreath, v => setting.expandAlbumBreath = v, false);
        yield return BoolChange("animationAdaptBPM", () => setting.animationAdaptBPM, v => setting.animationAdaptBPM = v, false);
    }

    private static SettingChange Change(string key, Func<string> read, Action<string> write, string target) =>
        new() { Key = key, Read = read, Write = write, Target = target };

    private static SettingChange BoolChange(string key, Func<bool> read, Action<bool> write, bool target) =>
        new() { Key = key, Read = () => read() ? "1" : "0", Write = v => write(v == "1"), Target = target ? "1" : "0" };

    private static SettingChange RuntimeChange(string key, Func<bool> read, Action<bool> write, bool target) =>
        new() { Key = key, Read = () => read() ? "1" : "0", Write = v => write(v == "1"), Target = target ? "1" : "0", Runtime = true };

    private static async Task RunOnUiAsync(Func<Task> action)
    {
        var done = new TaskCompletionSource<bool>();
        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
        {
            try
            {
                await action();
            }
            catch
            {
            }
            finally
            {
                done.TrySetResult(true);
            }
        });
        await done.Task;
    }
}
