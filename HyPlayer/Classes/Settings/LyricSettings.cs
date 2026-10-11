using HyPlayer.Classes;
using HyPlayer.UWP.Chopin;
using HyPlayer.UWP.Chopin.Abstractions.Models;
using Windows.Storage;
using Windows.UI;

namespace HyPlayer.Classes.Settings
{
    /// <summary>
    /// Settings related to lyric display, rendering, and behavior.
    /// </summary>
    public partial class LyricSettings : SettingsBase
    {
        /// <summary>
        /// Romaji source for lyric transliteration.
        /// </summary>
        public RomajiSource LyricRomajiSource
        {
            get => GetSettings(nameof(LyricRomajiSource), RomajiSource.None);
            set
            {
                SettingsBase.SetSettings(nameof(LyricRomajiSource), (int)value);
            }
        }

        /// <summary>
        /// Whether high-precision lyric timer is enabled.
        /// </summary>
        public bool highPreciseLyricTimer
        {
            get => GetSettings(nameof(highPreciseLyricTimer), false);
            set => SettingsBase.SetSettings(nameof(highPreciseLyricTimer), value);
        }

        /// <summary>
        /// Whether karaoke lyric mode is enabled.
        /// </summary>
        public bool karaokLyric
        {
            get => GetSettings(nameof(karaokLyric), true);
            set
            {
                SettingsBase.SetSettings(nameof(karaokLyric), value);
            }
        }

        /// <summary>
        /// Whether to show composer info in lyrics.
        /// </summary>
        public bool showComposerInLyric
        {
            get => GetSettings(nameof(showComposerInLyric), true);
            set
            {
                SettingsBase.SetSettings(nameof(showComposerInLyric), value);
            }
        }

        /// <summary>
        /// Whether to download lyrics.
        /// </summary>
        public bool downloadLyric
        {
            get => GetSettings(nameof(downloadLyric), true);
            set
            {
                SettingsBase.SetSettings(nameof(downloadLyric), value);
            }
        }

        /// <summary>
        /// Whether to download translations.
        /// </summary>
        public bool downloadTranslation
        {
            get => GetSettings(nameof(downloadTranslation), true);
            set
            {
                SettingsBase.SetSettings(nameof(downloadTranslation), value);
            }
        }

        /// <summary>
        /// Whether to migrate lyrics format.
        /// </summary>
        public bool MigrateLyrics
        {
            get => GetSettings(nameof(MigrateLyrics), false);
            set
            {
                SettingsBase.SetSettings(nameof(MigrateLyrics), value);
            }
        }

        /// <summary>
        /// Whether to optimize lyric display.
        /// </summary>
        public bool OptimizeLyric
        {
            get => GetSettings(nameof(OptimizeLyric), false);
            set
            {
                SettingsBase.SetSettings(nameof(OptimizeLyric), value);
            }
        }

        /// <summary>
        /// Whether lyric drop shadow is enabled.
        /// </summary>
        public bool lyricDropshadow
        {
            get => GetSettings(nameof(lyricDropshadow), false);
            set => SettingsBase.SetSettings(nameof(lyricDropshadow), value);
        }

        /// <summary>
        /// Whether lyric render target caching is enabled.
        /// </summary>
        public bool lyricCacheRenderTarget
        {
            get => GetSettings(nameof(lyricCacheRenderTarget), false);
            set
            {
                SettingsBase.SetSettings(nameof(lyricCacheRenderTarget), value);
            }
        }

        /// <summary>
        /// Lyric scale size.
        /// </summary>
        public int lyricScaleSize
        {
            get => GetSettings(nameof(lyricScaleSize), 3);
            set
            {
                SettingsBase.SetSettings(nameof(lyricScaleSize), value);
            }
        }

        /// <summary>
        /// Lyric font family.
        /// </summary>
        public string lyricFontFamily
        {
            get => GetSettings(nameof(lyricFontFamily), "Microsoft YaHei UI");
            set
            {
                SettingsBase.SetSettings(nameof(lyricFontFamily), value);
            }
        }

        /// <summary>
        /// Lyric line spacing.
        /// </summary>
        public int lyricLineSpacing
        {
            get => GetSettings(nameof(lyricLineSpacing), 0);
            set
            {
                SettingsBase.SetSettings(nameof(lyricLineSpacing), value);
            }
        }

        /// <summary>
        /// Translation font size.
        /// </summary>
        public int translationSize
        {
            get => GetSettings(nameof(translationSize), 0);
            set
            {
                SettingsBase.SetSettings(nameof(translationSize), value);
            }
        }

        /// <summary>
        /// Romaji font size.
        /// </summary>
        public int romajiSize
        {
            get => GetSettings(nameof(romajiSize), 15);
            set
            {
                SettingsBase.SetSettings(nameof(romajiSize), value);
            }
        }

        /// <summary>
        /// Lyric padding top ratio.
        /// </summary>
        public int lyricPaddingTopRatio
        {
            get => GetSettings(nameof(lyricPaddingTopRatio), 30);
            set
            {
                SettingsBase.SetSettings(nameof(lyricPaddingTopRatio), value);
            }
        }

        /// <summary>
        /// Lyric fading ratio.
        /// </summary>
        public int lyricFadingRatio
        {
            get => GetSettings(nameof(lyricFadingRatio), 5);
            set
            {
                SettingsBase.SetSettings(nameof(lyricFadingRatio), value);
            }
        }

        /// <summary>
        /// Whether hot lyric starts on startup.
        /// </summary>
        public bool hotlyricOnStartup
        {
            get => GetSettings(nameof(hotlyricOnStartup), false);
            set
            {
                SettingsBase.SetSettings(nameof(hotlyricOnStartup), value);
            }
        }

        /// <summary>
        /// Whether AMLL TTML database is enabled.
        /// </summary>
        public bool enableAmllTtmlDb
        {
            get => GetSettings(nameof(enableAmllTtmlDb), false);
            set
            {
                SettingsBase.SetSettings(nameof(enableAmllTtmlDb), value);
            }
        }

        /// <summary>
        /// AMLL TTML mirror URL.
        /// </summary>
        public const string DefaultAmllTtmlMirrorUrl = "https://gcore.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/ncm-lyrics/[NCM_ID].ttml";

        public string amllTtmlMirrorUrl
        {
            get => GetSettings(nameof(amllTtmlMirrorUrl), DefaultAmllTtmlMirrorUrl);
            set
            {
                SettingsBase.SetSettings(nameof(amllTtmlMirrorUrl), value);
            }
        }

        /// <summary>
        /// Whether lyric render focus highlighting is enabled.
        /// </summary>
        public bool lyricRenderFocusHighlighting
        {
            get => GetSettings(nameof(lyricRenderFocusHighlighting), true);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderFocusHighlighting), value);
            }
        }

        /// <summary>
        /// Lyric render width ratio.
        /// </summary>
        public int lyricRenderWidthRatio
        {
            get => GetSettings(nameof(lyricRenderWidthRatio), 80);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderWidthRatio), value);
            }
        }

        /// <summary>
        /// Whether lyric render transliteration scanning is enabled.
        /// </summary>
        public bool lyricRenderTransliterationScanning
        {
            get => GetSettings(nameof(lyricRenderTransliterationScanning), true);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderTransliterationScanning), value);
            }
        }

        /// <summary>
        /// Whether lyric render simple line scanning is enabled.
        /// </summary>
        public bool lyricRenderSimpleLineScanning
        {
            get => GetSettings(nameof(lyricRenderSimpleLineScanning), false);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderSimpleLineScanning), value);
            }
        }

        /// <summary>
        /// Whether lyric render scale when focusing is enabled.
        /// </summary>
        public bool lyricRenderScaleWhenFocusing
        {
            get => GetSettings(nameof(lyricRenderScaleWhenFocusing), true);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderScaleWhenFocusing), value);
            }
        }

        /// <summary>
        /// Whether lyric render blur is enabled.
        /// </summary>
        public bool lyricRenderBlur
        {
            get => GetSettings(nameof(lyricRenderBlur), true);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderBlur), value);
            }
        }

        /// <summary>
        /// Whether lyric render fade is enabled.
        /// </summary>
        public bool lyricRenderFade
        {
            get => GetSettings(nameof(lyricRenderFade), true);
            set
            {
                SettingsBase.SetSettings(nameof(lyricRenderFade), value);
            }
        }

        /// <summary>
        /// Line rolling calculator type.
        /// </summary>
        public RollingCalculator LineRollingCalculator
        {
            get => GetSettings(nameof(LineRollingCalculator), RollingCalculator.ElasticEaseRollingCalculator);
            set
            {
                SettingsBase.SetSettings(nameof(LineRollingCalculator), (int)value);
            }
        }

        /// <summary>
        /// Whether lyric renderer debug mode is enabled.
        /// </summary>
        public bool LyricRendererDebugMode
        {
            get => GetSettings(nameof(LyricRendererDebugMode), false);
            set
            {
                SettingsBase.SetSettings(nameof(LyricRendererDebugMode), value);
            }
        }

        /// <summary>
        /// Lyric renderer FPS.
        /// </summary>
        public int LyricRendererFPS
        {
            get => GetSettings(nameof(LyricRendererFPS), 60);
            set
            {
                SettingsBase.SetSettings(nameof(LyricRendererFPS), value);
            }
        }

#nullable enable
        /// <summary>
        /// Pure lyric idle color override.
        /// </summary>
        public Color? pureLyricIdleColor
        {
            get
            {
                var bytes = GetSettings<byte[]?>(nameof(pureLyricIdleColor), null);
                return bytes == null ? null : Color.FromArgb(bytes[0], bytes[1], bytes[2], bytes[3]);
            }
            set
            {
                if (value.HasValue)
                    SettingsBase.SetSettings(nameof(pureLyricIdleColor), new[]
                        { value.Value.A, value.Value.R, value.Value.G, value.Value.B });
                else SettingsBase.SetSettings(nameof(pureLyricIdleColor), null);
            }
        }

        /// <summary>
        /// Pure lyric focusing color override.
        /// </summary>
        public Color? pureLyricFocusingColor
        {
            get
            {
                var bytes = GetSettings<byte[]?>(nameof(pureLyricFocusingColor), null);
                return bytes == null ? null : Color.FromArgb(bytes[0], bytes[1], bytes[2], bytes[3]);
            }
            set
            {
                if (value.HasValue)
                    SettingsBase.SetSettings(nameof(pureLyricFocusingColor), new[]
                        { value.Value.A, value.Value.R, value.Value.G, value.Value.B });
                else SettingsBase.SetSettings(nameof(pureLyricFocusingColor), null);
            }
        }

        /// <summary>
        /// Karaoke lyric focusing color override.
        /// </summary>
        public Color? karaokLyricFocusingColor
        {
            get
            {
                var bytes = GetSettings<byte[]?>(nameof(karaokLyricFocusingColor), null);
                return bytes == null ? null : Color.FromArgb(bytes[0], bytes[1], bytes[2], bytes[3]);
            }
            set
            {
                if (value.HasValue)
                    SettingsBase.SetSettings(nameof(karaokLyricFocusingColor), new[]
                        { value.Value.A, value.Value.R, value.Value.G, value.Value.B });
                else SettingsBase.SetSettings(nameof(karaokLyricFocusingColor), null);
            }
        }
#nullable restore

        /// <summary>
        /// Whether Isolation full throttle mode is enabled.
        /// </summary>
        public bool IsolationFullThrottle
        {
            get => GetSettings(nameof(IsolationFullThrottle), true);
            set
            {
                SettingsBase.SetSettings(nameof(IsolationFullThrottle), value);
            }
        }

        public bool autoHideLyricPanel
        {
            get => GetSettings(nameof(autoHideLyricPanel), true);
            set => SettingsBase.SetSettings(nameof(autoHideLyricPanel), value);
        }

        public double autoHideLyricPanelLines
        {
            get => System.Math.Max(GetSettings(nameof(autoHideLyricPanelLines), 3d), 0d);
            set => SettingsBase.SetSettings(nameof(autoHideLyricPanelLines), System.Math.Max(System.Math.Round(value), 0d));
        }

        /// <summary>
        /// Isolation FPS (minimum 60).
        /// </summary>
        public double IsolationFPS
        {
            get => System.Math.Max(GetSettings(nameof(IsolationFPS), 60d), 60d);
            set
            {
                SettingsBase.SetSettings(nameof(IsolationFPS), value);
            }
        }

        /// <summary>
        /// Isolation scale factor.
        /// </summary>
        public float IsolationScale
        {
            get => GetSettings(nameof(IsolationScale), 1f);
            set
            {
                SettingsBase.SetSettings(nameof(IsolationScale), value);
            }
        }

        /// <summary>
        /// Whether Isolation light wave effect is enabled.
        /// </summary>
        public bool IsolationLightWave
        {
            get => GetSettings(nameof(IsolationLightWave), false);
            set
            {
                SettingsBase.SetSettings(nameof(IsolationLightWave), value);
            }
        }

        /// <summary>
        /// Whether Impressionist uses LAB color space.
        /// </summary>
        public bool ImpressionistLABSpace
        {
            get => GetSettings(nameof(ImpressionistLABSpace), true);
            set
            {
                SettingsBase.SetSettings(nameof(ImpressionistLABSpace), value);
            }
        }

        /// <summary>
        /// Whether Impressionist ignores white colors.
        /// </summary>
        public bool ImpressionistIgnoreWhite
        {
            get => GetSettings(nameof(ImpressionistIgnoreWhite), true);
            set
            {
                SettingsBase.SetSettings(nameof(ImpressionistIgnoreWhite), value);
            }
        }

        /// <summary>
        /// Whether Impressionist uses KMeans++ algorithm.
        /// </summary>
        public bool ImpressionistUseKMeansPP
        {
            get => GetSettings(nameof(ImpressionistUseKMeansPP), true);
            set
            {
                SettingsBase.SetSettings(nameof(ImpressionistUseKMeansPP), value);
            }
        }
    }
}
