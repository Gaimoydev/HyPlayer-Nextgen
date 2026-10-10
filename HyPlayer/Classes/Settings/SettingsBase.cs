using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Storage;

namespace HyPlayer.Classes.Settings
{
    /// <summary>
    /// Base class for sub-settings groups providing shared storage and notification infrastructure.
    /// </summary>
    public abstract class SettingsBase
    {
        private static readonly object MissingValue = new();

        private static readonly ConcurrentDictionary<string, object> Cache = new();

        public static T GetSettings<T>(string propertyName, T defaultValue)
        {
            try
            {
                var value = Cache.GetOrAdd(propertyName, static key =>
                    ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var stored) && stored != null
                        ? stored
                        : MissingValue);
                if (ReferenceEquals(value, MissingValue)) return defaultValue;
                if (value is T typed) return typed;
                if (typeof(T) == typeof(bool) && value is string text && bool.TryParse(text, out var flag))
                    return (T)(object)flag;
                return (T)value;
            }
            catch
            {
                return defaultValue;
            }
        }

#nullable enable
        public static void SetSettings(string propertyName, object? value)
        {
            ApplicationData.Current.LocalSettings.Values[propertyName] = value;
            if (value is null) Cache.TryRemove(propertyName, out _);
            else Cache[propertyName] = value;
        }
#nullable restore
    }
}
