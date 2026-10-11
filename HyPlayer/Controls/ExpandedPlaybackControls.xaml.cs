using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using HyPlayer.Services.Playback;
using HyPlayer.ViewModels;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace HyPlayer.Controls;

public sealed partial class ExpandedPlaybackControls : UserControl
{
    private readonly IUIStateService _uiState = Ioc.Default.GetRequiredService<IUIStateService>();
    private readonly IAuthService _auth = Ioc.Default.GetRequiredService<IAuthService>();
    private readonly Setting _setting = Ioc.Default.GetRequiredService<Setting>();
    private readonly PlaybackStateService _playbackState = Ioc.Default.GetRequiredService<PlaybackStateService>();
    private bool _showingTransition;
    private int _qualityVersion;
    private HyPlayer.Controls.Primitives.TextGlowEffect? _textGlow;
    private PlayBarViewModel? _viewModel;
    private long _lastElapsedSecond = -1;
    private long _lastRemainingSecond = -1;

    public ExpandedPlaybackControls()
    {
        InitializeComponent();
        ProgressBar.ValueDragging += ProgressBar_ValueDragging;
        ProgressBar.ValueCommitted += ProgressBar_ValueCommitted;
        VolumeBar.ValueDragging += VolumeBar_ValueChanged;
        VolumeBar.ValueCommitted += VolumeBar_ValueChanged;
        if (AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox")
            ItemDesktopLyrics.Visibility = Visibility.Collapsed;
    }

    private PlayBar? PlayBar => _uiState.BarPlayBar as PlayBar;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) return;
        _viewModel = PlayBar?.ViewModel;
        if (_viewModel is null) return;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _playbackState.PropertyChanged += PlaybackState_PropertyChanged;
        VolumeBar.Value = (PlayBar?.VolumeValue ?? 0) / 100;
        RefreshAll();
        RefreshQuality(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _playbackState.PropertyChanged -= PlaybackState_PropertyChanged;
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayBarViewModel.ProgressMilliseconds):
                RefreshProgress();
                break;
            case nameof(PlayBarViewModel.DurationMilliseconds):
                _lastRemainingSecond = -1;
                RefreshProgress();
                break;
            case nameof(PlayBarViewModel.PlayStateGlyph):
                RefreshPlayState();
                break;
            case nameof(PlayBarViewModel.IsInFm):
                RefreshFmState();
                RefreshPlayMode();
                break;
            case nameof(PlayBarViewModel.ActiveStrategyId):
                RefreshPlayMode();
                break;
            case nameof(PlayBarViewModel.NowPlayingItem):
                _lastElapsedSecond = -1;
                _lastRemainingSecond = -1;
                RefreshProgress();
                RefreshQuality(true);
                break;
            case nameof(PlayBarViewModel.QualityTagText):
                RefreshQuality(true);
                break;
        }
    }

    private void RefreshAll()
    {
        _lastElapsedSecond = -1;
        _lastRemainingSecond = -1;
        RefreshProgress();
        RefreshPlayState();
        RefreshFmState();
        RefreshPlayMode();
    }

    private void RefreshPlayMode()
    {
        if (_viewModel is null) return;
        var (glyph, text) = _viewModel.IsInFm
            ? ("\uE107", "我不喜欢")
            : _viewModel.ActiveStrategyId switch
            {
                "shn" => ("\uE14B", "随机播放"),
                "sgl" => ("\uE1CC", "单曲循环"),
                _ => ("\uE169", "顺序播放")
            };
        IconPlayMode.Glyph = glyph;
        ToolTipService.SetToolTip(BtnPlayMode, text);
    }

    private void BtnPlayMode_Click(object sender, RoutedEventArgs e)
    {
        PlayBar?.SwitchPlayMode();
        RefreshPlayMode();
    }

    private void PlaybackState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaybackStateService.IsTransitioning)) return;
        _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => RefreshQuality(true));
    }

    private void RefreshQuality(bool animate)
    {
        var transitioning = _playbackState.IsTransitioning;
        var text = transitioning ? "过渡" : _viewModel?.NowPlayingItem is null ? string.Empty : _viewModel.QualityTagText;
        if (QualityText.Text == text && _showingTransition == transitioning) return;
        _showingTransition = transitioning;
        _textGlow ??= new HyPlayer.Controls.Primitives.TextGlowEffect(QualityRevealHost, QualityMaskSource, QualityRevealText, QualityBloomHost, QualityFillHost);
        ProgressBar.IsGlowing = transitioning;
        AnimateQualityHost(transitioning);
        if (!transitioning) _textGlow.SetActive(false);
        if (!animate || string.IsNullOrEmpty(QualityText.Text))
        {
            QualityText.Text = text;
            QualityText.Opacity = transitioning ? 0 : 1;
            if (transitioning) _textGlow.SetActive(true);
            return;
        }
        var version = ++_qualityVersion;
        var visual = Windows.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(QualityText);
        var compositor = visual.Compositor;
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1f, 0f);
        fadeOut.Duration = TimeSpan.FromMilliseconds(150);
        var batch = compositor.CreateScopedBatch(Windows.UI.Composition.CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fadeOut);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (version != _qualityVersion) return;
            QualityText.Text = text;
            QualityText.Opacity = transitioning ? 0 : 1;
            if (transitioning) _textGlow.SetActive(true);
            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(1f, 1f);
            fadeIn.Duration = TimeSpan.FromMilliseconds(240);
            visual.StartAnimation("Opacity", fadeIn);
        };
    }

    private bool? _qualityHostBright;

    private void AnimateQualityHost(bool bright)
    {
        if (_qualityHostBright == bright) return;
        var visual = Windows.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(QualityHost);
        if (_qualityHostBright is null)
        {
            visual.Opacity = bright ? 1f : 0.6f;
            _qualityHostBright = bright;
            return;
        }
        _qualityHostBright = bright;
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, bright ? 1f : 0.6f);
        fade.Duration = TimeSpan.FromMilliseconds(500);
        visual.StartAnimation("Opacity", fade);
    }

    private void RefreshPlayState()
    {
        if (_viewModel is null) return;
        IconPlayPause.Glyph = _viewModel.PlayStateGlyph;
    }

    private void RefreshFmState()
    {
        if (_viewModel is null) return;
        IconPrevious.Glyph = _viewModel.IsInFm ? "" : "";
    }

    private void RefreshProgress()
    {
        if (_viewModel is null || ProgressBar.IsDragging) return;
        var duration = _viewModel.DurationMilliseconds;
        var position = _viewModel.ProgressMilliseconds;
        ProgressBar.Value = duration > 0 ? position / duration : 0;
        UpdateTimeTexts(position, duration);
    }

    private void UpdateTimeTexts(double position, double duration)
    {
        position = Math.Max(0, position);
        var remaining = Math.Max(0, duration - position);
        var elapsedSecond = (long)(position / 1000);
        var remainingSecond = (long)Math.Ceiling(remaining / 1000);
        if (elapsedSecond != _lastElapsedSecond)
        {
            _lastElapsedSecond = elapsedSecond;
            ElapsedText.Text = FormatTime(elapsedSecond);
        }

        if (remainingSecond != _lastRemainingSecond)
        {
            _lastRemainingSecond = remainingSecond;
            RemainingText.Text = "-" + FormatTime(remainingSecond);
        }
    }

    private static string FormatTime(long totalSeconds)
    {
        var time = TimeSpan.FromSeconds(totalSeconds);
        return time.Hours == 0 ? $"{time.Minutes}:{time.Seconds:00}" : $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private void ProgressBar_ValueDragging(double value)
    {
        if (_viewModel is null) return;
        var duration = _viewModel.DurationMilliseconds;
        UpdateTimeTexts(value * duration, duration);
    }

    private void ProgressBar_ValueCommitted(double value)
    {
        if (_viewModel is null) return;
        var duration = _viewModel.DurationMilliseconds;
        if (duration <= 0) return;
        _viewModel.SeekCommand.Execute(TimeSpan.FromMilliseconds(value * duration));
    }

    private void VolumeBar_ValueChanged(double value)
    {
        PlayBar?.SetVolumeValue(Math.Round(value * 100));
    }

    private void BtnPrevious_Click(object sender, RoutedEventArgs e) => PlayBar?.PlayPrevious();

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => PlayBar?.TogglePlayback();

    private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayBar?.PlayNext();

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    private void MoreFlyout_Opening(object sender, object e)
    {
        var item = _viewModel?.NowPlayingItem;
        var isNetease = _viewModel?.CanShareCurrentSong ?? false;
        var isLiked = item is not null && _auth.LikedSongs.Contains(item.Id);
        ItemLike.Text = isLiked ? "取消喜欢" : "喜欢";
        ItemLikeIcon.Glyph = isLiked ? "" : "";
        ItemLike.Visibility = isNetease ? Visibility.Visible : Visibility.Collapsed;
        ItemSubscribe.Visibility = isNetease ? Visibility.Visible : Visibility.Collapsed;
        ItemDownload.Visibility = isNetease ? Visibility.Visible : Visibility.Collapsed;
        ItemShare.Visibility = isNetease ? Visibility.Visible : Visibility.Collapsed;
        ItemComment.Visibility = isNetease && _setting.notClearMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemLike_Click(object sender, RoutedEventArgs e) => PlayBar?.LikeCurrent();

    private void ItemPlaylist_Click(object sender, RoutedEventArgs e) => PlayBar?.ShowPlaylistAt(BtnMore);

    private void ItemDesktopLyrics_Click(object sender, RoutedEventArgs e) => PlayBar?.OpenDesktopLyrics();

    private void ItemSubscribe_Click(object sender, RoutedEventArgs e) => PlayBar?.AddCurrentToSongList();

    private void ItemDownload_Click(object sender, RoutedEventArgs e) => PlayBar?.DownloadCurrent();

    private void ItemComment_Click(object sender, RoutedEventArgs e) => PlayBar?.OpenComments();

    private void ItemShare_Click(object sender, RoutedEventArgs e) => PlayBar?.ShareCurrent();
}
