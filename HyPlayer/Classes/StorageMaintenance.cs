#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;

namespace HyPlayer.Classes;

public enum StorageContentKind
{
    Music,
    Cache
}

public sealed record StorageItemInfo(StorageFile File, string RelativeFolder, ulong Size, DateTimeOffset Modified);

public sealed record StorageMigrationResult(int Copied, int Skipped, int Failed);

public static class StorageMaintenance
{
    private static readonly string[] MusicExtensions = [".flac", ".mp3", ".ncm", ".ape", ".m4a", ".wav", ".ogg", ".opus", ".aac", ".lrc", ".yrc", ".ttml", ".alrc", ".lys"];
    private static readonly string[] CacheAudioExtensions = [".flac", ".mp3", ".m4a", ".ogg", ".aac", ".wav"];
    private static readonly HashSet<string> CacheFolderNames = new(Enum.GetNames<CacheType>(), StringComparer.OrdinalIgnoreCase);
    private const int MaxMusicDepth = 4;
    private static readonly SemaphoreSlim TrimLock = new(1, 1);
    private static int _trimScheduled;
    private static DateTime _lastTrim = DateTime.MinValue;

    public static bool IsCacheFolderName(string name) => CacheFolderNames.Contains(name);

    private static bool IsCacheAudioFile(string name)
    {
        var extension = Path.GetExtension(name);
        if (Array.FindIndex(CacheAudioExtensions, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)) < 0) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length > 0 && stem.All(char.IsAsciiDigit);
    }

    private static bool IsMusicFile(string name)
    {
        var extension = Path.GetExtension(name);
        return Array.FindIndex(MusicExtensions, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)) >= 0;
    }

    private static async Task<IReadOnlyList<StorageFile>> GetFilesWithPropertiesAsync(StorageFolder folder)
    {
        var options = new QueryOptions { FolderDepth = FolderDepth.Shallow };
        options.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties, []);
        return await folder.CreateFileQueryWithOptions(options).GetFilesAsync();
    }

    public static async Task<List<StorageItemInfo>> CollectAsync(StorageFolder root, StorageContentKind kind, string? excludePath = null, CancellationToken ct = default)
    {
        var result = new List<StorageItemInfo>();
        if (kind == StorageContentKind.Cache)
        {
            await AddFilesAsync(root, "", IsCacheAudioFile);
            foreach (var folder in await root.GetFoldersAsync())
            {
                if (!IsCacheFolderName(folder.Name) || IsSamePath(folder.Path, excludePath)) continue;
                await AddFilesAsync(folder, folder.Name, _ => true);
            }
        }
        else
        {
            await WalkMusicAsync(root, "", 0);
        }
        return result;

        async Task WalkMusicAsync(StorageFolder folder, string relative, int depth)
        {
            await AddFilesAsync(folder, relative, IsMusicFile);
            if (depth >= MaxMusicDepth) return;
            foreach (var child in await folder.GetFoldersAsync())
            {
                if (IsSamePath(child.Path, excludePath)) continue;
                await WalkMusicAsync(child, relative.Length == 0 ? child.Name : Path.Combine(relative, child.Name), depth + 1);
            }
        }

        async Task AddFilesAsync(StorageFolder folder, string relative, Func<string, bool> filter)
        {
            foreach (var file in await GetFilesWithPropertiesAsync(folder))
            {
                ct.ThrowIfCancellationRequested();
                if (!filter(file.Name)) continue;
                try
                {
                    var properties = await file.GetBasicPropertiesAsync();
                    result.Add(new StorageItemInfo(file, relative, properties.Size, properties.DateModified));
                }
                catch
                {
                }
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> AccessCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsUsableFolder(string? path, string? accessToken)
    {
        if (string.IsNullOrEmpty(path) || BelongsToOtherPackage(path)) return false;
        return AccessCache.GetOrAdd(path, p => CheckAccess(p, accessToken));
    }

    public static void ForgetAccess(string? path)
    {
        if (!string.IsNullOrEmpty(path)) AccessCache.TryRemove(path, out _);
    }

    private static bool CheckAccess(string path, string? accessToken)
    {
        try
        {
            if (IsInsidePackageData(path)) return true;
            var music = KnownFolders.MusicLibrary.Path;
            if (!string.IsNullOrEmpty(music) && IsNestedPath(path, music) && path.StartsWith(Path.TrimEndingDirectorySeparator(music), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
        }

        try
        {
            var list = Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList;
            if (accessToken != null && list.ContainsItem(accessToken))
            {
                var granted = list.GetFolderAsync(accessToken).AsTask().GetAwaiter().GetResult();
                if (IsSamePath(granted.Path, path) || IsNestedPath(path, granted.Path) && path.StartsWith(Path.TrimEndingDirectorySeparator(granted.Path), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (var entry in list.Entries)
            {
                try
                {
                    var folder = list.GetFolderAsync(entry.Token).AsTask().GetAwaiter().GetResult();
                    if (path.StartsWith(Path.TrimEndingDirectorySeparator(folder.Path), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        try
        {
            StorageFolder.GetFolderFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInsidePackageData(string path)
    {
        var own = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(ApplicationData.Current.LocalFolder.Path) ?? string.Empty);
        return own.Length > 0 && (Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool BelongsToOtherPackage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(packages, StringComparison.OrdinalIgnoreCase)) return false;
        var own = Path.Combine(packages, Windows.ApplicationModel.Package.Current.Id.FamilyName) + Path.DirectorySeparatorChar;
        return !(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).StartsWith(own, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsNestedPath(string a, string b)
    {
        var first = Path.TrimEndingDirectorySeparator(a) + Path.DirectorySeparatorChar;
        var second = Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar;
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<StorageMigrationResult> MigrateAsync(StorageFolder from, StorageFolder to, IReadOnlyList<StorageItemInfo> items, bool deleteOriginal, IProgress<int>? progress = null)
    {
        int copied = 0, skipped = 0, failed = 0, done = 0;
        var folders = new Dictionary<string, StorageFolder>(StringComparer.OrdinalIgnoreCase) { [""] = to };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            try
            {
                var destination = await GetFolderAsync(item.RelativeFolder);
                var moved = false;
                if (await destination.TryGetItemAsync(item.File.Name) is StorageFile existing)
                {
                    if ((await existing.GetBasicPropertiesAsync()).Size == item.Size)
                    {
                        moved = true;
                        copied++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    await item.File.CopyAsync(destination, item.File.Name, NameCollisionOption.FailIfExists);
                    moved = true;
                    copied++;
                }

                if (moved && deleteOriginal)
                {
                    try
                    {
                        await item.File.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        if (item.RelativeFolder.Length > 0) touched.Add(item.RelativeFolder);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                failed++;
            }
            progress?.Report(++done);
        }

        if (deleteOriginal)
        {
            foreach (var relative in touched.OrderByDescending(r => r.Length))
            {
                try
                {
                    var current = relative;
                    while (current.Length > 0)
                    {
                        if (await from.TryGetItemAsync(current) is not StorageFolder folder) break;
                        if ((await folder.GetItemsAsync(0, 1)).Count > 0) break;
                        await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        current = Path.GetDirectoryName(current) ?? "";
                    }
                }
                catch
                {
                }
            }
        }

        return new StorageMigrationResult(copied, skipped, failed);

        async Task<StorageFolder> GetFolderAsync(string relative)
        {
            if (folders.TryGetValue(relative, out var cached)) return cached;
            var parent = await GetFolderAsync(Path.GetDirectoryName(relative) ?? "");
            var created = await parent.CreateFolderAsync(Path.GetFileName(relative), CreationCollisionOption.OpenIfExists);
            folders[relative] = created;
            return created;
        }
    }

    public static string FormatSize(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => $"{bytes / 1024 / 1024 / 1024:0.##} GB",
        >= 1024d * 1024 => $"{bytes / 1024 / 1024:0.#} MB",
        _ => $"{bytes / 1024:0} KB"
    };

    public static async Task<ulong> GetCacheUsageAsync()
    {
        var root = await StorageFolder.GetFolderFromPathAsync(Ioc.Default.GetRequiredService<Setting>().cacheDir);
        ulong total = 0;
        foreach (var item in await CollectAsync(root, StorageContentKind.Cache)) total += item.Size;
        return total;
    }

    public static void ScheduleTrim(TimeSpan? delay = null)
    {
        if (Interlocked.Exchange(ref _trimScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var wait = delay ?? TimeSpan.FromSeconds(5);
                var sinceLast = DateTime.UtcNow - _lastTrim;
                if (sinceLast < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1) - sinceLast;
                await Task.Delay(wait);
                Volatile.Write(ref _trimScheduled, 0);
                await TrimCacheAsync();
            }
            catch
            {
                Volatile.Write(ref _trimScheduled, 0);
            }
        });
    }

    public static async Task TrimCacheAsync()
    {
        if (!await TrimLock.WaitAsync(0)) return;
        try
        {
            _lastTrim = DateTime.UtcNow;
            var setting = Ioc.Default.GetRequiredService<Setting>();
            var limit = (ulong)(setting.cacheLimitGB * 1024 * 1024 * 1024);
            if (limit == 0) return;
            var root = await StorageFolder.GetFolderFromPathAsync(setting.cacheDir);
            var items = await CollectAsync(root, StorageContentKind.Cache);
            ulong total = 0;
            foreach (var item in items) total += item.Size;
            if (total <= limit) return;
            var target = limit / 10 * 9;
            foreach (var item in items.OrderBy(i => i.Modified))
            {
                if (total <= target) break;
                try
                {
                    await item.File.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    total -= item.Size;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            TrimLock.Release();
        }
    }
}
