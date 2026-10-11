using HyPlayer.Classes;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;

using HyPlayer.Services.Abstractions;
using CommunityToolkit.Mvvm.DependencyInjection;
namespace HyPlayer.Classes.Settings
{
    /// <summary>
    /// Settings related to UI appearance, themes, animations, and visual effects.
    /// </summary>
    public partial class UISettings : SettingsBase
    {
        /// <summary>
        /// Theme request (Auto, Light, Dark).
        /// </summary>
        public ThemeRequest themeRequest
        {
            get => GetSettings(nameof(themeRequest), ThemeRequest.Auto);
            set
            {
                SettingsBase.SetSettings(nameof(themeRequest), (int)value);
            }
        }

        /// <summary>
        /// Whether expand animation is enabled.
        /// </summary>
        public bool expandAnimation
        {
            get => GetSettings(nameof(expandAnimation), true);
            set
            {
                SettingsBase.SetSettings(nameof(expandAnimation), value ? "true" : "false");
            }
        }

        /// <summary>
        /// Whether to force memory garbage collection.
        /// </summary>
        public int BackgroundReleaseSeconds
        {
            get => GetSettings(nameof(BackgroundReleaseSeconds), 60);
            set => SettingsBase.SetSettings(nameof(BackgroundReleaseSeconds), value);
        }

        public int MemoryLimitMB
        {
            get => GetSettings(nameof(MemoryLimitMB), -1);
            set => SettingsBase.SetSettings(nameof(MemoryLimitMB), value);
        }

        public bool forceMemoryGarbage
        {
            get => GetSettings(nameof(forceMemoryGarbage), false);
            set => SettingsBase.SetSettings(nameof(forceMemoryGarbage), value);
        }

        /// <summary>
        /// Whether to disable image loading.
        /// </summary>
        public bool noImage
        {
            get => GetSettings(nameof(noImage), false);
            set => SettingsBase.SetSettings(nameof(noImage), value);
        }

        /// <summary>
        /// Lyric alignment setting.
        /// </summary>
        public LyricAlignment lyricAlignment
        {
            get => GetSettings(nameof(lyricAlignment), LyricAlignment.Left);
            set
            {
                SettingsBase.SetSettings(nameof(lyricAlignment), (int)value);
            }
        }

        /// <summary>
        /// Lyric font size override (0 = default).
        /// </summary>
        public int lyricSize
        {
            get => GetSettings(nameof(lyricSize), 0);
            set
            {
                SettingsBase.SetSettings(nameof(lyricSize), value);
            }
        }

        /// <summary>
        /// Lyric color mode.
        /// </summary>
        public LyricColor lyricColor
        {
            get => GetSettings(nameof(lyricColor), LyricColor.Auto);
            set => SettingsBase.SetSettings(nameof(lyricColor), (int)value);
        }

        /// <summary>
        /// Color generator type for theming.
        /// </summary>
        public ColorGeneratorType ColorGeneratorType
        {
            get => GetSettings(nameof(ColorGeneratorType), ColorGeneratorType.Auto);
            set
            {
                SettingsBase.SetSettings(nameof(ColorGeneratorType), (int)value);
            }
        }

        /// <summary>
        /// Whether old theme is enabled.
        /// </summary>
        public bool IsOldThemeEnabled
        {
            get => GetSettings(nameof(IsOldThemeEnabled), false);
            set
            {
                SettingsBase.SetSettings(nameof(IsOldThemeEnabled), value);
            }
        }

        /// <summary>
        /// Expanded player background type.
        /// </summary>
        public BackgroundType expandedPlayerBackgroundType
        {
            get => GetSettings(nameof(expandedPlayerBackgroundType), BackgroundType.CoverBlur);
            set
            {
                SettingsBase.SetSettings(nameof(expandedPlayerBackgroundType), (int)value);
            }
        }

        /// <summary>
        /// Whether custom acrylic is enabled.
        /// </summary>
        public bool CustomAcrylic
        {
            get => GetSettings(nameof(CustomAcrylic), false);
            set
            {
                SettingsBase.SetSettings(nameof(CustomAcrylic), value);
            }
        }

        /// <summary>
        /// Custom tint opacity for acrylic.
        /// </summary>
        public double CustomTintOpacity
        {
            get
            {
                try
                {
                    if (CustomAcrylic)
                    {
                        return GetSettings(nameof(CustomTintOpacity), 3d);
                    }
                    else
                    {
                        return 0d;
                    }
                }
                catch
                {
                    return 3d;
                }
            }
            set => SettingsBase.SetSettings(nameof(CustomTintOpacity), value);
        }

        /// <summary>
        /// Custom tint luminosity opacity for acrylic.
        /// </summary>
        public double CustomTintLuminosityOpacity
        {
            get
            {
                try
                {
                    if (CustomAcrylic)
                    {
                        return GetSettings(nameof(CustomTintLuminosityOpacity), 3d);
                    }
                    else
                    {
                        return 0d;
                    }
                }
                catch
                {
                    return 3d;
                }
            }
            set => SettingsBase.SetSettings(nameof(CustomTintLuminosityOpacity), value);
        }

        /// <summary>
        /// Whether acrylic background is enabled.
        /// </summary>
        public bool acrylicBackgroundStatus
        {
            get => GetSettings(nameof(acrylicBackgroundStatus), false);
            set
            {
                SettingsBase.SetSettings(nameof(acrylicBackgroundStatus), value);
            }
        }

        /// <summary>
        /// Whether acrylic effects are available on this system.
        /// </summary>
        public static bool acrylicAvailabiliity => new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled && Windows.UI.Composition.CompositionCapabilities.GetForCurrentView().AreEffectsFast();

        /// <summary>
        /// Whether album cover rotates during playback.
        /// </summary>
        public bool albumRotate
        {
            get => GetSettings(nameof(albumRotate), false);
            set
            {
                SettingsBase.SetSettings(nameof(albumRotate), value);
                if (value) albumRound = true;
            }
        }

        /// <summary>
        /// Whether album cover is displayed as round.
        /// </summary>
        public bool albumRound
        {
            get => GetSettings(nameof(albumRound), false);
            set
            {
                SettingsBase.SetSettings(nameof(albumRound), value);
                if (!value) albumRotate = false;
            }
        }

        /// <summary>
        /// Album border length.
        /// </summary>
        public int albumBorderLength
        {
            get => GetSettings(nameof(albumBorderLength), 0);
            set => SettingsBase.SetSettings(nameof(albumBorderLength), value);
        }

        /// <summary>
        /// Whether expanded player uses acrylic.
        /// </summary>
        public bool expandedUseAcrylic
        {
            get => GetSettings(nameof(expandedUseAcrylic), true);
            set => SettingsBase.SetSettings(nameof(expandedUseAcrylic), value);
        }

        /// <summary>
        /// Whether playbar background has breath animation.
        /// </summary>
        public bool playbarBackgroundBreath
        {
            get => GetSettings(nameof(playbarBackgroundBreath), false);
            set => SettingsBase.SetSettings(nameof(playbarBackgroundBreath), value);
        }

        /// <summary>
        /// Whether playbar background uses acrylic.
        /// </summary>
        public bool playbarBackgroundAcrylic
        {
            get => GetSettings(nameof(playbarBackgroundAcrylic), false);
            set
            {
                SettingsBase.SetSettings(nameof(playbarBackgroundAcrylic), value);
            }
        }

        /// <summary>
        /// Whether expanded album has breath animation.
        /// </summary>
        public bool expandAlbumBreath
        {
            get => GetSettings(nameof(expandAlbumBreath), false);
            set => SettingsBase.SetSettings(nameof(expandAlbumBreath), value);
        }

        /// <summary>
        /// Whether list header uses acrylic blur.
        /// </summary>
        public bool listHeaderAcrylicBlur
        {
            get => GetSettings(nameof(listHeaderAcrylicBlur), true);
            set => SettingsBase.SetSettings(nameof(listHeaderAcrylicBlur), value);
        }

        /// <summary>
        /// Whether list item background uses acrylic blur.
        /// </summary>
        public bool itemOfListBackgroundAcrylicBlur
        {
            get => GetSettings(nameof(itemOfListBackgroundAcrylicBlur), false);
            set => SettingsBase.SetSettings(nameof(itemOfListBackgroundAcrylicBlur), value);
        }

        /// <summary>
        /// Whether playbar buttons are transparent.
        /// </summary>
        public bool playbarButtonsTransparent
        {
            get => GetSettings(nameof(playbarButtonsTransparent), true);
            set
            {
                SettingsBase.SetSettings(nameof(playbarButtonsTransparent), value);
            }
        }

        /// <summary>
        /// Whether playbar background has elay effect.
        /// </summary>
        public bool playbarBackgroundElay
        {
            get => GetSettings(nameof(playbarBackgroundElay), false);
            set
            {
                SettingsBase.SetSettings(nameof(playbarBackgroundElay), value);
            }
        }

        public bool playbarProgressiveBlur
        {
            get => GetSettings(nameof(playbarProgressiveBlur), true);
            set
            {
                SettingsBase.SetSettings(nameof(playbarProgressiveBlur), value);
            }
        }

        /// <summary>
        /// Whether play button uses accent color.
        /// </summary>
        public bool playButtonAccentColor
        {
            get => GetSettings(nameof(playButtonAccentColor), false);
            set
            {
                SettingsBase.SetSettings(nameof(playButtonAccentColor), value);
            }
        }

        /// <summary>
        /// Whether expanded player shows full cover.
        /// </summary>
        public bool expandedPlayerFullCover
        {
            get => GetSettings(nameof(expandedPlayerFullCover), false);
            set
            {
                SettingsBase.SetSettings(nameof(expandedPlayerFullCover), value);
            }
        }

        /// <summary>
        /// Expanded cover shadow depth.
        /// </summary>
        public int expandedCoverShadowDepth
        {
            get => GetSettings(nameof(expandedCoverShadowDepth), 4);
            set
            {
                SettingsBase.SetSettings(nameof(expandedCoverShadowDepth), value);
            }
        }

        /// <summary>
        /// Whether title bar immerse mode is enabled.
        /// </summary>
        public bool EnableTitleBarImmerse
        {
            get => GetSettings("enableTitleBarImmerse", true);
            set
            {
                SettingsBase.SetSettings("enableTitleBarImmerse", value);
            }
        }

        /// <summary>
        /// Whether compact player page blur is enabled.
        /// </summary>
        public bool CompactPlayerPageBlurStatus
        {
            get => GetSettings(nameof(CompactPlayerPageBlurStatus), false);
            set
            {
                SettingsBase.SetSettings(nameof(CompactPlayerPageBlurStatus), value);
            }
        }

        /// <summary>
        /// Whether not-clear mode is enabled.
        /// </summary>
        public bool notClearMode
        {
            get => GetSettings(nameof(notClearMode), true);
            set
            {
                SettingsBase.SetSettings(nameof(notClearMode), value);
            }
        }

        /// <summary>
        /// Whether playbar auto-hides.
        /// </summary>
        public string customUiFontFamily
        {
            get => GetSettings(nameof(customUiFontFamily), string.Empty);
            set => SettingsBase.SetSettings(nameof(customUiFontFamily), value ?? string.Empty);
        }

        public string backgroundImagePath
        {
            get => GetSettings(nameof(backgroundImagePath), string.Empty);
            set => SettingsBase.SetSettings(nameof(backgroundImagePath), value ?? string.Empty);
        }

        public double backgroundImageOpacity
        {
            get => GetSettings(nameof(backgroundImageOpacity), 35d);
            set => SettingsBase.SetSettings(nameof(backgroundImageOpacity), value);
        }

        public double backgroundImageBlur
        {
            get => GetSettings(nameof(backgroundImageBlur), 0d);
            set => SettingsBase.SetSettings(nameof(backgroundImageBlur), value);
        }

        public int backgroundImageStretch
        {
            get => GetSettings(nameof(backgroundImageStretch), 0);
            set => SettingsBase.SetSettings(nameof(backgroundImageStretch), value);
        }

        public bool hidePlaybarOnHome
        {
            get => GetSettings(nameof(hidePlaybarOnHome), false);
            set => SettingsBase.SetSettings(nameof(hidePlaybarOnHome), value);
        }

        public bool AutoHidePlaybar
        {
            get => GetSettings(nameof(AutoHidePlaybar), false);
            set
            {
                SettingsBase.SetSettings(nameof(AutoHidePlaybar), value);
            }
        }

        public ExpandedControlStyle expandedControlStyle
        {
            get => GetSettings(nameof(expandedControlStyle), ExpandedControlStyle.Apple);
            set => SettingsBase.SetSettings(nameof(expandedControlStyle), (int)value);
        }

        public bool expandedAutoCollapsePlaybar
        {
            get => GetSettings(nameof(expandedAutoCollapsePlaybar), true);
            set => SettingsBase.SetSettings(nameof(expandedAutoCollapsePlaybar), value);
        }

        /// <summary>
        /// Auto-hide playbar timeout in seconds.
        /// </summary>
        public int AutoHidePlaybarTime
        {
            get => GetSettings(nameof(AutoHidePlaybarTime), 3);
            set
            {
                SettingsBase.SetSettings(nameof(AutoHidePlaybarTime), value);
                Ioc.Default.GetRequiredService<IUIStateService>().PlaybarSecondCounter = 0;
            }
        }

        /// <summary>
        /// Whether playbar has margin.
        /// </summary>
        public bool playBarMargin
        {
            get => GetSettings(nameof(playBarMargin), true);
            set
            {
                SettingsBase.SetSettings(nameof(playBarMargin), value);
            }
        }

        /// <summary>
        /// Whether UI sounds are enabled.
        /// </summary>
        public bool uiSound
        {
            get => GetSettings(nameof(uiSound), false);
            set
            {
                SettingsBase.SetSettings(nameof(uiSound), value);
            }
        }

        /// <summary>
        /// Whether to display shuffled list.
        /// </summary>
        public bool displayShuffledList
        {
            get => GetSettings(nameof(displayShuffledList), true);
            set
            {
                SettingsBase.SetSettings(nameof(displayShuffledList), value);
            }
        }

        /// <summary>
        /// Whether to display maintenance info.
        /// </summary>
        public bool displayMaintain
        {
            get => GetSettings(nameof(displayMaintain), false);
            set
            {
                SettingsBase.SetSettings(nameof(displayMaintain), value);
            }
        }

        /// <summary>
        /// Whether to hide pointer on Xbox.
        /// </summary>
        public bool xboxHidePointer
        {
            get => GetSettings(nameof(xboxHidePointer), false);
            set => SettingsBase.SetSettings(nameof(xboxHidePointer), value);
        }

        /// <summary>
        /// Whether touch gesture actions are enabled.
        /// </summary>
        public bool enableTouchGestureAction
        {
            get => GetSettings(nameof(enableTouchGestureAction), false);
            set => SettingsBase.SetSettings(nameof(enableTouchGestureAction), value);
        }

        /// <summary>
        /// Gesture mode.
        /// </summary>
        public GestureMode gestureMode
        {
            get => GetSettings(nameof(gestureMode), GestureMode.Basic);
            set => SettingsBase.SetSettings(nameof(gestureMode), (int)value);
        }

        /// <summary>
        /// Whether animation adapts to BPM.
        /// </summary>
        public bool animationAdaptBPM
        {
            get => GetSettings(nameof(animationAdaptBPM), false);
            set
            {
                SettingsBase.SetSettings(nameof(animationAdaptBPM), value);
            }
        }

        /// <summary>
        /// Whether gentle BPM animation is enabled.
        /// </summary>
        public bool gentleBPMAnimation
        {
            get => GetSettings(nameof(gentleBPMAnimation), false);
            set
            {
                SettingsBase.SetSettings(nameof(gentleBPMAnimation), value);
            }
        }

        /// <summary>
        /// Whether popup notifications are disabled.
        /// </summary>
        public bool DisablePopUp
        {
            get => GetSettings(nameof(DisablePopUp), false);
            set => SettingsBase.SetSettings(nameof(DisablePopUp), value);
        }

        /// <summary>
        /// Whether tile is enabled.
        /// </summary>
        public bool enableTile
        {
            get => GetSettings(nameof(enableTile), System.Environment.OSVersion.Version.Build < 22000);
            set
            {
                SettingsBase.SetSettings(nameof(enableTile), value);
                if (!value)
                {
                    tileBackgroundAvailability = false;
                    saveTileBackgroundToLocalFolder = false;
                }
            }
        }

        /// <summary>
        /// Whether tile background is available.
        /// </summary>
        public bool tileBackgroundAvailability
        {
            get => GetSettings(nameof(tileBackgroundAvailability), false);
            set
            {
                SettingsBase.SetSettings(nameof(tileBackgroundAvailability), value);
            }
        }

        /// <summary>
        /// Whether to save tile background to local folder.
        /// </summary>
        public bool saveTileBackgroundToLocalFolder
        {
            get => GetSettings(nameof(saveTileBackgroundToLocalFolder), false);
            set
            {
                SettingsBase.SetSettings(nameof(saveTileBackgroundToLocalFolder), value);
            }
        }

        /// <summary>
        /// Whether canary channel is available.
        /// </summary>
        public bool canaryChannelAvailability
        {
            get => GetSettings(nameof(canaryChannelAvailability), false);
            set
            {
                SettingsBase.SetSettings(nameof(canaryChannelAvailability), value);
            }
        }

        /// <summary>
        /// Whether local progressive loading is enabled.
        /// </summary>
        public bool localProgressiveLoad
        {
            get => GetSettings(nameof(localProgressiveLoad), false);
            set
            {
                SettingsBase.SetSettings(nameof(localProgressiveLoad), value);
            }
        }

        /// <summary>
        /// Update source channel.
        /// </summary>
        public UpdateSource UpdateSource
        {
            get => GetSettings(nameof(UpdateSource), UpdateSource.Release);
            set
            {
                SettingsBase.SetSettings(nameof(UpdateSource), (int)value);
            }
        }
    }
}
