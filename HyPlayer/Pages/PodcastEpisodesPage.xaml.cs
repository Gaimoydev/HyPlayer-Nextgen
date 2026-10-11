#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.Classes;
using HyPlayer.Services.Abstractions;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace HyPlayer.Pages;

public sealed partial class PodcastEpisodesPage : Page
{
    public const string Liked = "liked";
    public const string Collected = "collected";

    private readonly CancellationTokenSource _cts = new();
    public ObservableCollection<NCSong> Songs { get; } = new();
    public string Kind { get; private set; } = Liked;

    public PodcastEpisodesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Kind = e.Parameter as string == Collected ? Collected : Liked;
        TitleText.Text = Kind == Collected ? "收藏的单集" : "点赞的单集";
        SubtitleText.Text = "正在加载…";
        SongContainer.IsLoading = true;
        try
        {
            var contract = Kind == Collected ? PodcastListApi.CollectedVoices : PodcastListApi.LikedVoices;
            var episodes = await PodcastLibrary.GetEpisodesAsync(contract, _cts.Token);
            foreach (var episode in episodes) Songs.Add(episode);
            SubtitleText.Text = Songs.Count == 0 ? "这里还没有单集" : $"共 {Songs.Count} 个单集";
            PlayAllButton.IsEnabled = Songs.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SubtitleText.Text = "加载失败";
            Ioc.Default.GetRequiredService<INotificationService>().ShowMessage("加载播客单集失败", ex.Message);
        }
        finally
        {
            SongContainer.IsLoading = false;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts.Cancel();
    }

    private async void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        if (Songs.Count == 0) return;
        var playlist = Ioc.Default.GetRequiredService<IPlaylistService>();
        playlist.AppendNcSongs(Songs, clearFirst: true);
        playlist.PlaySourceId = string.Empty;
        if (playlist.Items.Count > 0) await playlist.MoveToAsync(playlist.Items[0]);
    }
}
