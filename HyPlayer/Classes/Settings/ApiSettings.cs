using HyPlayer.Classes;
using HyPlayer.NeteaseApi;
using System.Text.Json;
using Windows.Storage;

using CommunityToolkit.Mvvm.DependencyInjection;
namespace HyPlayer.Classes.Settings
{
    /// <summary>
    /// Settings related to API configuration, proxy, and network options.
    /// </summary>
    public partial class ApiSettings : SettingsBase
    {
        /// <summary>
        /// Whether proxy is enabled.
        /// </summary>
        public bool EnableProxy
        {
            get => GetSettings(nameof(EnableProxy), false);
            set
            {
                SettingsBase.SetSettings(nameof(EnableProxy), value);
            }
        }

        /// <summary>
        /// Additional API parameters.
        /// </summary>
        public AdditionalParameters ApiAdditionalParameters
        {
            get => JsonSerializer.Deserialize<AdditionalParameters>(GetSettings(nameof(ApiAdditionalParameters), "{}"), JsonDefaults.Options) ?? new AdditionalParameters();
            set => SettingsBase.SetSettings(nameof(ApiAdditionalParameters), JsonSerializer.Serialize(value, JsonDefaults.Options));
        }

        /// <summary>
        /// Whether to use HTTP instead of HTTPS.
        /// </summary>
        public bool UseHttp
        {
            get => GetSettings(nameof(UseHttp), false);
            set
            {
                SettingsBase.SetSettings(nameof(UseHttp), value);
            }
        }

        /// <summary>
        /// Whether to use HTTP when getting songs.
        /// </summary>
        public bool UseHttpWhenGettingSongs
        {
            get => GetSettings(nameof(UseHttpWhenGettingSongs), false);
            set
            {
                SettingsBase.SetSettings(nameof(UseHttpWhenGettingSongs), value);
            }
        }

        /// <summary>
        /// Whether to enable fake check token API.
        /// </summary>
        public bool EnableCheckTokenApi
        {
            get => GetSettings(nameof(EnableCheckTokenApi), false);
            set
            {
                SettingsBase.SetSettings(nameof(EnableCheckTokenApi), value);
                Ioc.Default.GetRequiredService<NeteaseCloudMusicApiHandler>()?.Option.FakeCheckToken = value;
            }
        }

        /// <summary>
        /// Whether API caching is enabled.
        /// </summary>
        public bool enableApiCache
        {
            get => GetSettings(nameof(enableApiCache), false);
            set
            {
                SettingsBase.SetSettings(nameof(enableApiCache), value);
            }
        }

        /// <summary>
        /// Whether to lazily get song URLs.
        /// </summary>
        public bool songUrlLazyGet
        {
            get => GetSettings(nameof(songUrlLazyGet), true);
            set
            {
                SettingsBase.SetSettings(nameof(songUrlLazyGet), value);
            }
        }

        /// <summary>
        /// Whether to greedily load play container items.
        /// </summary>
        public bool greedlyLoadPlayContainerItems
        {
            get => GetSettings(nameof(greedlyLoadPlayContainerItems), false);
            set
            {
                SettingsBase.SetSettings(nameof(greedlyLoadPlayContainerItems), value);
            }
        }

        /// <summary>
        /// Whether to auto-add greedily loaded songs to playlist.
        /// </summary>
        public bool AutoAddGreedilyLoadedSongsToPlayList
        {
            get => GetSettings(nameof(AutoAddGreedilyLoadedSongsToPlayList), false);
            set
            {
                SettingsBase.SetSettings(nameof(AutoAddGreedilyLoadedSongsToPlayList), value);
            }
        }

        /// <summary>
        /// Whether to skip VIP songs during playback.
        /// </summary>
        public bool jumpVipSongPlaying
        {
            get => GetSettings(nameof(jumpVipSongPlaying), false);
            set
            {
                SettingsBase.SetSettings(nameof(jumpVipSongPlaying), value);
            }
        }

        /// <summary>
        /// Whether to skip VIP songs during download.
        /// </summary>
        public bool jumpVipSongDownloading
        {
            get => GetSettings(nameof(jumpVipSongDownloading), false);
            set
            {
                SettingsBase.SetSettings(nameof(jumpVipSongDownloading), value);
            }
        }
    }
}
