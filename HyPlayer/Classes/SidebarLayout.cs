#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using HyPlayer.Classes.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace HyPlayer.Classes;

public sealed class SidebarEntry : ObservableObject
{
    private bool _isVisible = true;

    public SidebarEntry(string key, string name, NavigationViewItemBase item)
    {
        Key = key;
        Name = name;
        Item = item;
    }

    public string Key { get; }
    public string Name { get; }
    internal NavigationViewItemBase Item { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value)) SidebarLayout.OnChanged();
        }
    }
}

public sealed class SidebarSection : ObservableObject
{
    private bool _isVisible = true;

    public SidebarSection(string key, string name, NavigationViewItemHeader header)
    {
        Key = key;
        Name = name;
        Header = header;
        Items.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move) SidebarLayout.OnChanged();
        };
    }

    public string Key { get; }
    public string Name { get; }
    internal NavigationViewItemHeader Header { get; }
    public ObservableCollection<SidebarEntry> Items { get; } = new();

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value)) SidebarLayout.OnChanged();
        }
    }
}

public static class SidebarLayout
{
    private const string SettingKey = "SidebarLayout";
    private static readonly Dictionary<string, string> SectionKeys = new()
    {
        ["发现"] = "discover",
        ["音乐"] = "music",
        ["歌单"] = "playlist",
        ["播客"] = "podcast"
    };

    private static NavigationView? _navigation;
    private static List<(string Section, string[] Items)> _defaultOrder = [];
    private static bool _suspend;

    public static ObservableCollection<SidebarSection> Sections { get; } = new();

    static SidebarLayout()
    {
        Sections.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move) OnChanged();
        };
    }

    public static void Attach(NavigationView navigation)
    {
        _navigation = navigation;
        _suspend = true;
        Sections.Clear();
        SidebarSection? current = null;
        foreach (var element in navigation.MenuItems.OfType<NavigationViewItemBase>())
        {
            if (element is NavigationViewItemHeader header)
            {
                var name = header.Content as string ?? string.Empty;
                current = new SidebarSection(SectionKeys.GetValueOrDefault(name, name), name, header);
                Sections.Add(current);
                continue;
            }
            if (current == null || element is NavigationViewItemSeparator || element is not NavigationViewItem item) continue;
            var key = item.Tag as string ?? item.Name;
            if (string.IsNullOrEmpty(key) || item.Content is not string text) continue;
            current.Items.Add(new SidebarEntry(key, text, item));
        }

        _defaultOrder = Sections.Select(s => (s.Key, s.Items.Select(i => i.Key).ToArray())).ToList();
        Restore(SettingsBase.GetSettings(SettingKey, string.Empty));
        _suspend = false;
        Apply();
    }

    public static void ResetToDefault()
    {
        SettingsBase.SetSettings(SettingKey, string.Empty);
        _suspend = true;
        Restore(string.Empty);
        foreach (var section in Sections)
        {
            section.IsVisible = true;
            foreach (var entry in section.Items) entry.IsVisible = true;
        }
        _suspend = false;
        Apply();
    }

    internal static void OnChanged()
    {
        if (_suspend) return;
        Save();
        Apply();
    }

    private static void Restore(string saved)
    {
        var sections = Sections.ToDictionary(s => s.Key);
        var parsed = Parse(saved);
        var order = parsed.Count > 0
            ? parsed.Select(p => p.Section).Concat(_defaultOrder.Select(d => d.Section)).Distinct().ToList()
            : _defaultOrder.Select(d => d.Section).ToList();

        var reordered = order.Where(sections.ContainsKey).Select(k => sections[k]).ToList();
        Sections.Clear();
        foreach (var section in reordered)
        {
            var state = parsed.FirstOrDefault(p => p.Section == section.Key);
            section.IsVisible = state.Section == null || state.Visible;
            var entries = section.Items.ToDictionary(i => i.Key);
            var defaults = _defaultOrder.First(d => d.Section == section.Key).Items;
            var itemOrder = (state.Items ?? []).Select(i => i.Key).Concat(defaults).Distinct().Where(entries.ContainsKey).ToList();
            section.Items.Clear();
            foreach (var key in itemOrder)
            {
                var entry = entries[key];
                var itemState = state.Items?.FirstOrDefault(i => i.Key == key);
                entry.IsVisible = itemState?.Visible ?? true;
                section.Items.Add(entry);
            }
            Sections.Add(section);
        }
    }

    private static void Save()
    {
        var text = string.Join(";", Sections.Select(s =>
            $"{s.Key}={(s.IsVisible ? 1 : 0)}:{string.Join(",", s.Items.Select(i => $"{i.Key}={(i.IsVisible ? 1 : 0)}"))}"));
        SettingsBase.SetSettings(SettingKey, text);
    }

    private static List<(string Section, bool Visible, List<(string Key, bool Visible)>? Items)> Parse(string saved)
    {
        var result = new List<(string, bool, List<(string, bool)>?)>();
        foreach (var part in saved.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            var head = colon < 0 ? part : part[..colon];
            var pair = head.Split('=');
            if (pair.Length != 2) continue;
            var items = new List<(string, bool)>();
            if (colon >= 0)
            {
                foreach (var item in part[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = item.Split('=');
                    if (kv.Length == 2) items.Add((kv[0], kv[1] != "0"));
                }
            }
            result.Add((pair[0], pair[1] != "0", items));
        }
        return result;
    }

    private static void Apply()
    {
        if (_navigation == null) return;
        var menu = _navigation.MenuItems;
        var desired = new List<object>();
        foreach (var section in Sections.Where(s => s.IsVisible))
        {
            var visible = section.Items.Where(i => i.IsVisible).Select(i => (object)i.Item).ToList();
            if (visible.Count == 0) continue;
            if (desired.Count > 0) desired.Add(new NavigationViewItemSeparator());
            desired.Add(section.Header);
            desired.AddRange(visible);
        }

        var selected = _navigation.SelectedItem;
        menu.Clear();
        foreach (var item in desired) menu.Add(item);
        if (selected != null && desired.Contains(selected)) _navigation.SelectedItem = selected;
    }
}
