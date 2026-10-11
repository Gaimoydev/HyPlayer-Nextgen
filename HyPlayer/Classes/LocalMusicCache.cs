#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace HyPlayer.Classes;

public static class LocalMusicCache
{
    private const string CacheFileName = "local_music_cache.tsv";

    private static string Clean(string? value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public static async Task SaveAsync(IEnumerable<HyPlayItem> items)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Url)) continue;
                builder.Append(Clean(item.Url)).Append('\t')
                    .Append(Clean(item.Name)).Append('\t')
                    .Append(Clean(string.Join("; ", item.Artist?.Select(a => a.Name) ?? []))).Append('\t')
                    .Append(Clean(item.Album?.Name)).Append('\t')
                    .Append(((long)item.LengthInMilliseconds).ToString()).Append('\t')
                    .Append(Clean(item.SubExt)).Append('\n');
            }
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(CacheFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, builder.ToString());
        }
        catch
        {
        }
    }

    public static async Task<List<HyPlayItem>?> LoadAsync()
    {
        try
        {
            if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(CacheFileName) is not StorageFile file) return null;
            var items = new List<HyPlayItem>();
            foreach (var line in await FileIO.ReadLinesAsync(file))
            {
                var parts = line.Split('\t');
                if (parts.Length < 6 || string.IsNullOrEmpty(parts[0])) continue;
                var artists = parts[2].Split("; ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => new NCArtist { Name = name, Type = HyPlayItemType.Local }).ToList();
                if (artists.Count == 0) artists.Add(new NCArtist { Name = "未知歌手", Type = HyPlayItemType.Local });
                items.Add(new HyPlayItem
                {
                    ItemType = HyPlayItemType.LocalProgressive,
                    IsLocalFile = true,
                    Url = parts[0],
                    Name = string.IsNullOrEmpty(parts[1]) ? System.IO.Path.GetFileNameWithoutExtension(parts[0]) : parts[1],
                    Artist = artists,
                    Album = new NCAlbum { Name = parts[3], AlbumType = HyPlayItemType.Local },
                    LengthInMilliseconds = long.TryParse(parts[4], out var length) ? length : 0,
                    SubExt = parts[5],
                    CDName = "01",
                    InfoTag = "本地歌曲"
                });
            }
            return items;
        }
        catch
        {
            return null;
        }
    }
}
