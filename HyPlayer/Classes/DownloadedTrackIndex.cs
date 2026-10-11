#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.HyPlayControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Search;

namespace HyPlayer.Classes;

public static class DownloadedTrackIndex
{
    private const string IndexFileName = "downloaded_tracks.tsv";
    private static readonly string[] AudioExtensions = [".flac", ".mp3", ".m4a", ".ogg", ".wav", ".ape"];
    private static readonly ConcurrentDictionary<string, string> Entries = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task? _loading;
    private static int _scanScheduled;

    private static Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private static async Task LoadAsync()
    {
        try
        {
            if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(IndexFileName) is not StorageFile file) return;
            foreach (var line in await FileIO.ReadLinesAsync(file))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                Entries[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch
        {
        }
    }

    private static async Task SaveAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var builder = new StringBuilder();
            foreach (var (id, path) in Entries) builder.Append(id).Append('\t').Append(path).Append('\n');
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(IndexFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, builder.ToString());
        }
        catch
        {
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task RecordAsync(string? songId, IStorageFile? file)
    {
        if (string.IsNullOrEmpty(songId) || file is null) return;
        await EnsureLoadedAsync();
        Entries[songId] = file.Path;
        await SaveAsync();
    }

    public static async Task<StorageFile?> TryGetAsync(string? songId)
    {
        if (string.IsNullOrEmpty(songId)) return null;
        if (!Ioc.Default.GetRequiredService<Setting>().preferDownloadedFile) return null;
        await EnsureLoadedAsync();
        if (!Entries.TryGetValue(songId, out var path)) return null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if ((await file.GetBasicPropertiesAsync()).Size > 0) return file;
        }
        catch
        {
        }
        if (Entries.TryRemove(songId, out _)) _ = SaveAsync();
        return null;
    }

    public static void ScheduleScan(TimeSpan delay)
    {
        if (Interlocked.Exchange(ref _scanScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await ScanAsync();
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref _scanScheduled, 0);
            }
        });
    }

    private static async Task ScanAsync()
    {
        await EnsureLoadedAsync();
        var setting = Ioc.Default.GetRequiredService<Setting>();
        var known = new HashSet<string>(Entries.Values, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var path in new[] { setting.downloadDir, setting.searchingDir }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(path)) continue;
            StorageFolder folder;
            try
            {
                folder = await StorageFolder.GetFolderFromPathAsync(path);
            }
            catch
            {
                continue;
            }
            var options = new QueryOptions(CommonFileQuery.DefaultQuery, AudioExtensions) { FolderDepth = FolderDepth.Deep };
            IReadOnlyList<StorageFile> files;
            try
            {
                files = await folder.CreateFileQueryWithOptions(options).GetFilesAsync();
            }
            catch
            {
                continue;
            }
            foreach (var file in files)
            {
                if (known.Contains(file.Path)) continue;
                known.Add(file.Path);
                var id = ReadTrackId(file);
                if (id is null) continue;
                Entries[id] = file.Path;
                changed = true;
            }
        }
        if (changed) await SaveAsync();
    }

    private static string? ReadTrackId(StorageFile file)
    {
        try
        {
            using var abstraction = new UwpStorageFileAbstraction(file);
            using var tagFile = TagLibHelper.Create(abstraction, file.FileType);
            return The163KeyHelper.TryGetTrackId(tagFile.Tag, out var id) ? id.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}
