#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.NeteaseApi;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HyPlayer.Classes;

public sealed record PodcastChannel(string Id, string Name, string? Cover);

internal static class PodcastLibrary
{
    public static async Task<List<NCFmItem>> GetEpisodesAsync(PodcastListApi contract, CancellationToken token = default)
    {
        var api = Ioc.Default.GetRequiredService<NeteaseCloudMusicApiHandler>();
        var result = new List<NCFmItem>();
        var offset = 0;
        while (result.Count < 1000)
        {
            var response = await api.RequestAsync(contract, new PodcastListRequest { Limit = 200, Offset = offset }, token);
            if (response.IsError) throw new System.Exception(response.Error?.Message ?? "加载失败");
            if (!TryGetData(response.Value, out var data)) break;
            var page = Items(data).Select(MapEpisode).Where(t => t != null).Cast<NCFmItem>().ToList();
            foreach (var item in page)
            {
                item.Order = result.Count;
                result.Add(item);
            }
            if (page.Count == 0 || !Bool(data, "hasMore")) break;
            offset += page.Count;
            contract = Clone(contract);
        }
        return result;
    }

    public static async Task<List<PodcastChannel>> GetChannelsAsync(PodcastListApi contract, CancellationToken token = default)
    {
        var api = Ioc.Default.GetRequiredService<NeteaseCloudMusicApiHandler>();
        var response = await api.RequestAsync(contract, new PodcastListRequest { Limit = 200 }, token);
        if (response.IsError || !TryGetData(response.Value, out var data)) return [];
        var channels = new List<PodcastChannel>();
        foreach (var item in Items(data))
        {
            var id = Text(item, "voiceListId") ?? Text(item, "id");
            var name = Text(item, "voiceListName") ?? Text(item, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
            channels.Add(new PodcastChannel(id!, name!, Text(item, "coverUrl") ?? Text(item, "picUrl")));
        }
        return channels;
    }

    private static PodcastListApi Clone(PodcastListApi contract) => contract.ApiPath switch
    {
        "/api/content/my/liked/voice" => PodcastListApi.LikedVoices,
        "/api/content/my/collect/voice" => PodcastListApi.CollectedVoices,
        _ => contract
    };

    private static bool TryGetData(PodcastJsonResponse? response, out JsonElement data)
    {
        data = default;
        if (response?.Payload == null || !response.Payload.TryGetValue("data", out data)) return false;
        return data.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    private static IEnumerable<JsonElement> Items(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array) return data.EnumerateArray();
        foreach (var key in new[] { "data", "list" })
            if (data.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                return list.EnumerateArray();
        return [];
    }

    private static NCFmItem? MapEpisode(JsonElement voice)
    {
        var songId = Text(voice, "trackId");
        if (voice.TryGetProperty("mainSong", out var mainSong) && mainSong.ValueKind == JsonValueKind.Object)
            songId ??= Text(mainSong, "id");
        if (string.IsNullOrEmpty(songId) || songId == "0") return null;

        string? radioId = null, radioName = null, radioCover = null;
        if (voice.TryGetProperty("radio", out var radio) && radio.ValueKind == JsonValueKind.Object)
        {
            radioId = Text(radio, "id");
            radioName = Text(radio, "name");
            radioCover = Text(radio, "picUrl");
        }

        var dj = new NCArtist
        {
            Type = HyPlayItemType.Radio,
            Id = Text(voice, "userId"),
            Name = Text(voice, "userName") ?? ""
        };
        if (voice.TryGetProperty("dj", out var djElement) && djElement.ValueKind == JsonValueKind.Object)
            dj.Avatar = Text(djElement, "avatarUrl");

        return new NCFmItem
        {
            Type = HyPlayItemType.Radio,
            SongId = songId,
            SongName = Text(voice, "voiceName") ?? "",
            Artist = [dj],
            Album = new NCAlbum
            {
                AlbumType = HyPlayItemType.Radio,
                Id = radioId,
                Alias = radioId,
                Name = radioName ?? "",
                Cover = Text(voice, "coverUrl") ?? radioCover
            },
            LengthInMilliseconds = Number(voice, "duration"),
            MVId = "-1",
            FMId = Text(voice, "voiceId"),
            Description = Text(voice, "voiceDesc"),
            RadioId = radioId,
            RadioName = radioName
        };
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
