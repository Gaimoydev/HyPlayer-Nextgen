#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.Classes.Settings;
using HyPlayer.Controls;
using HyPlayer.Pages;
using HyPlayer.Services.Abstractions;
using HyPlayer.Services.Playback;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace HyPlayer.Classes;

public sealed partial class ShortcutEntry : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DefaultBinding { get; init; }
    public required bool Repeatable { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial string Binding { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial bool IsCapturing { get; set; }

    public string DisplayText => IsCapturing ? "请按下新的快捷键…" : string.IsNullOrEmpty(Binding) ? "未设置" : Binding.Replace("+", " + ");
}

public static class KeyboardShortcuts
{
    private const string SettingKey = "KeyboardShortcutBindings";
    private const string EnabledKey = "KeyboardShortcutsEnabled";
    private static bool _attached;
    private static ShortcutEntry? _capturing;

    public static ObservableCollection<ShortcutEntry> Entries { get; } = CreateEntries();

    public static bool Enabled
    {
        get => SettingsBase.GetSettings(EnabledKey, true);
        set => SettingsBase.SetSettings(EnabledKey, value);
    }

    private static ObservableCollection<ShortcutEntry> CreateEntries()
    {
        var entries = new ObservableCollection<ShortcutEntry>
        {
            new() { Id = "playpause", Name = "播放 / 暂停", DefaultBinding = "Space", Repeatable = false },
            new() { Id = "previous", Name = "上一首", DefaultBinding = "Ctrl+Left", Repeatable = false },
            new() { Id = "next", Name = "下一首", DefaultBinding = "Ctrl+Right", Repeatable = false },
            new() { Id = "seekback", Name = "后退 5 秒", DefaultBinding = "Ctrl+Shift+Left", Repeatable = true },
            new() { Id = "seekforward", Name = "快进 5 秒", DefaultBinding = "Ctrl+Shift+Right", Repeatable = true },
            new() { Id = "volumeup", Name = "音量 +5", DefaultBinding = "Ctrl+Up", Repeatable = true },
            new() { Id = "volumedown", Name = "音量 -5", DefaultBinding = "Ctrl+Down", Repeatable = true },
            new() { Id = "mute", Name = "静音", DefaultBinding = "Ctrl+M", Repeatable = false },
            new() { Id = "like", Name = "喜欢当前歌曲", DefaultBinding = "Ctrl+L", Repeatable = false },
            new() { Id = "lyrics", Name = "打开 / 收起歌词页", DefaultBinding = "Ctrl+E", Repeatable = false },
            new() { Id = "search", Name = "搜索", DefaultBinding = "Ctrl+F", Repeatable = false },
            new() { Id = "fullscreen", Name = "全屏", DefaultBinding = "F11", Repeatable = false }
        };
        var saved = Parse(SettingsBase.GetSettings(SettingKey, string.Empty));
        foreach (var entry in entries) entry.Binding = saved.TryGetValue(entry.Id, out var binding) ? binding : entry.DefaultBinding;
        return entries;
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index > 0) result[pair[..index]] = pair[(index + 1)..];
        }
        return result;
    }

    private static void Save()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries) builder.Append(entry.Id).Append('=').Append(entry.Binding).Append(';');
        SettingsBase.SetSettings(SettingKey, builder.ToString());
    }

    public static void Attach(CoreWindow window)
    {
        if (_attached) return;
        _attached = true;
        window.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
    }

    public static void BeginCapture(ShortcutEntry entry)
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = entry;
        entry.IsCapturing = true;
    }

    public static void Clear(ShortcutEntry entry)
    {
        entry.IsCapturing = false;
        if (ReferenceEquals(_capturing, entry)) _capturing = null;
        entry.Binding = string.Empty;
        Save();
    }

    public static void ResetAll()
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = null;
        foreach (var entry in Entries) entry.Binding = entry.DefaultBinding;
        Save();
    }

    private static void Assign(ShortcutEntry entry, string binding)
    {
        foreach (var other in Entries)
            if (!ReferenceEquals(other, entry) && string.Equals(other.Binding, binding, StringComparison.OrdinalIgnoreCase))
                other.Binding = string.Empty;
        entry.Binding = binding;
        entry.IsCapturing = false;
        _capturing = null;
        Save();
    }

    private static bool IsDown(VirtualKey key) =>
        (CoreWindow.GetForCurrentThread()?.GetKeyState(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static string Format(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)key - (int)VirtualKey.Number0).ToString(),
            _ => key.ToString()
        });
        return string.Join('+', parts);
    }

    private static bool IsTextInputFocused()
    {
        var focused = FocusManager.GetFocusedElement();
        return focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;
    }

    private static bool IsEditingKey(VirtualKey key, bool ctrl, bool alt) =>
        !alt && (key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End
            or VirtualKey.Back or VirtualKey.Delete or VirtualKey.Space || ctrl && key is VirtualKey.A or VirtualKey.C or VirtualKey.V or VirtualKey.X or VirtualKey.Z or VirtualKey.Y)
        || !ctrl && !alt && key is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.Number0 and <= VirtualKey.Number9;

    private static void OnAcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
    {
        if (args.EventType is not (CoreAcceleratorKeyEventType.KeyDown or CoreAcceleratorKeyEventType.SystemKeyDown)) return;
        var key = args.VirtualKey;
        if (IsModifier(key)) return;
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);

        if (_capturing is { } capturing)
        {
            args.Handled = true;
            if (args.KeyStatus.WasKeyDown) return;
            if (key == VirtualKey.Escape && !ctrl && !shift && !alt)
            {
                capturing.IsCapturing = false;
                _capturing = null;
                return;
            }
            Assign(capturing, Format(key, ctrl, shift, alt));
            return;
        }

        if (!Enabled) return;
        var binding = Format(key, ctrl, shift, alt);
        var entry = Entries.FirstOrDefault(e => string.Equals(e.Binding, binding, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        if (IsTextInputFocused() && IsEditingKey(key, ctrl, alt)) return;
        if (args.KeyStatus.WasKeyDown && !entry.Repeatable)
        {
            args.Handled = true;
            return;
        }
        args.Handled = Execute(entry.Id);
    }

    private static bool Execute(string id)
    {
        var uiState = Ioc.Default.GetRequiredService<IUIStateService>();
        var playBar = uiState.BarPlayBar as PlayBar;
        switch (id)
        {
            case "playpause":
                playBar?.TogglePlayback();
                return playBar is not null;
            case "previous":
                playBar?.PlayPrevious();
                return playBar is not null;
            case "next":
                playBar?.PlayNext();
                return playBar is not null;
            case "seekback":
            case "seekforward":
            {
                var state = Ioc.Default.GetRequiredService<PlaybackStateService>();
                var delta = TimeSpan.FromSeconds(id == "seekforward" ? 5 : -5);
                var target = state.Position + delta;
                if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                if (state.Duration > TimeSpan.Zero && target > state.Duration) target = state.Duration;
                _ = Ioc.Default.GetRequiredService<IPlaybackControlService>().SeekAsync(target);
                return true;
            }
            case "volumeup":
            case "volumedown":
                if (playBar is null) return false;
                playBar.SetVolumeValue(Math.Clamp(playBar.VolumeValue + (id == "volumeup" ? 5 : -5), 0, 100));
                return true;
            case "mute":
                playBar?.ToggleMute();
                return playBar is not null;
            case "like":
                playBar?.LikeCurrent();
                return playBar is not null;
            case "lyrics":
                if (playBar is null) return false;
                if (uiState.IsExpanded) playBar.CollapseExpandedPlayer();
                else playBar.ShowExpandedPlayer();
                return true;
            case "search":
                if (uiState.IsExpanded) playBar?.CollapseExpandedPlayer();
                return (uiState.PageBase as BasePage)?.FocusSearch() == true;
            case "fullscreen":
            {
                var view = ApplicationView.GetForCurrentView();
                if (view.IsFullScreenMode) view.ExitFullScreenMode();
                else view.TryEnterFullScreenMode();
                return true;
            }
        }
        return false;
    }
}
