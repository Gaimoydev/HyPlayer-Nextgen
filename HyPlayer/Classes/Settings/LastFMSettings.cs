using LiteFM.Abstractions;
using System.Text.Json;
using Windows.Storage;

namespace HyPlayer.Classes.Settings
{
    /// <summary>
    /// Settings related to Last.FM integration.
    /// </summary>
    public partial class LastFMSettings : SettingsBase
    {
        /// <summary>
        /// Last.FM session data.
        /// </summary>
        public LastFMSession LastFMSession
        {
            get => JsonSerializer.Deserialize<LastFMSession>(GetSettings(nameof(LastFMSession), "{}"), JsonDefaults.Options);
            set
            {
                if (value == null)
                {
                    SettingsBase.SetSettings(nameof(LastFMSession), null);
                }
                else
                {
                    SettingsBase.SetSettings(nameof(LastFMSession), JsonSerializer.Serialize(value, JsonDefaults.Options));
                }
            }
        }

        /// <summary>
        /// Whether to update Last.FM now playing status.
        /// </summary>
        public bool UpdateLastFMNowPlaying
        {
            get => GetSettings(nameof(UpdateLastFMNowPlaying), true);
            set
            {
                SettingsBase.SetSettings(nameof(UpdateLastFMNowPlaying), value);
            }
        }

        /// <summary>
        /// Whether Last.FM scrobbling is enabled.
        /// </summary>
        public bool LastFMScrobble
        {
            get => GetSettings(nameof(LastFMScrobble), true);
            set
            {
                SettingsBase.SetSettings(nameof(LastFMScrobble), value);
            }
        }

        /// <summary>
        /// Whether AI DJ mode is enabled.
        /// </summary>
        public bool useAiDj
        {
            get => GetSettings(nameof(useAiDj), false);
            set
            {
                SettingsBase.SetSettings(nameof(useAiDj), value);
            }
        }
    }
}
