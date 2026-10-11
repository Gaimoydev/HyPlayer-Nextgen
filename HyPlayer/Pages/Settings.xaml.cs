#region

using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using HyPlayer.Classes;
using HyPlayer.Controls.Primitives;
using HyPlayer.NeteaseApi;
using HyPlayer.Services.Abstractions;
using HyPlayer.Services.Playback.Models;
using Kawazu;
using LiteFM;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.BackgroundTransfer;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using WinRT;
using ItemsRepeater = Microsoft.UI.Xaml.Controls.ItemsRepeater;
using Point = Windows.Foundation.Point;
using SettingsExpander = CommunityToolkit.WinUI.Controls.SettingsExpander;
using StackLayout = Microsoft.UI.Xaml.Controls.StackLayout;

#endregion

// https://go.microsoft.com/fwlink/?LinkId=234238 上介绍了“空白页”项模板

namespace HyPlayer.Pages;

/// <summary>
///     可用于自身或导航至 Frame 内部的空白页。
/// </summary>
public sealed partial class Settings : Page
{
    private readonly Setting _setting = Ioc.Default.GetRequiredService<Setting>();
    private readonly NeteaseCloudMusicApiHandler _api = Ioc.Default.GetRequiredService<NeteaseCloudMusicApiHandler>();
    private readonly INotificationService _notification = Ioc.Default.GetRequiredService<INotificationService>();
    private readonly INavigationService _navigation = Ioc.Default.GetRequiredService<INavigationService>();

    public ModelManager Models { get; } = Ioc.Default.GetRequiredService<ModelManager>();

    private bool isbyprogram;
    private int _elapse = 10;


    public static readonly DependencyProperty IsAdvancedLyricColorSettingsShowProperty = DependencyProperty.Register(
        "IsAdvancedLyricColorSettingsShow", typeof(bool), typeof(Settings), new PropertyMetadata(default(bool)));

    public bool IsAdvancedLyricColorSettingsShow
    {
        get => (bool)GetValue(IsAdvancedLyricColorSettingsShowProperty);
        set => SetValue(IsAdvancedLyricColorSettingsShowProperty, value);
    }

    public Settings()
    {
        isbyprogram = true;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ = Models.RefreshAsync();

        RomajiStatus.Header = (Ioc.Default.GetRequiredService<IUIStateService>().KawazuConv == null ? "请下载Kawazu资源文件" : "可以转换");
        ButtonDownloadRomaji.Visibility = Ioc.Default.GetRequiredService<IUIStateService>().KawazuConv == null ? Visibility.Visible : Visibility.Collapsed;
        if (_setting.audioRate.EndsWith('0') || _setting.downloadAudioRate.EndsWith('0'))
        {
            _setting.audioRate = "exhigh";
            _setting.downloadAudioRate = "hires";
        }
        else
        {
            ComboBoxSongBr.SelectedIndex = ComboBoxSongBr.Items.IndexOf(ComboBoxSongBr.Items.First(t =>
                t?.As<ComboBoxItem>().Tag.ToString() == _setting.audioRate));
            ComboBoxSongDownloadBr.SelectedIndex = ComboBoxSongDownloadBr.Items.IndexOf(
                ComboBoxSongDownloadBr.Items.First(t =>
                    t?.As<ComboBoxItem>().Tag.ToString() == _setting.downloadAudioRate));
        }

        TextBoxXREALIP.Text = ApplicationData.Current.LocalSettings.Values["xRealIp"] != null
            ? ApplicationData.Current.LocalSettings.Values["xRealIp"].ToString()
            : "";
        var package = Package.Current;
        var packageId = package.Id;
        var version = packageId.Version;
        VersionCode.Text =
            $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}  (#{BuildInfo.CommitSha[..7]}@{BuildInfo.BuildBranchId})";
        var deviceInfo = new EasClientDeviceInformation();
        DeviceInfo.Text = deviceInfo.Id.ToString();
        isbyprogram = false;
#if DEBUG
        VersionCode.Text += " Debug";
#endif
        //ToggleButtonDaylight.IsChecked = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        BtnXboxReserve.Visibility = true ? Visibility.Visible : Visibility.Collapsed;
        FontBox.ItemsSource = SystemFonts.Value;
    }

    public static readonly Lazy<List<FontInfo>> SystemFonts = new(GetAllFonts, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private void Pivot_OnPivotItemLoaded(Pivot sender, PivotItemEventArgs args)
    {
        var item = args.Item;
        _ = Dispatcher.RunIdleAsync(_ignored =>
        {
            foreach (var expander in item.FindDescendants().OfType<SettingsExpander>())
            {
                if (ReferenceEquals(expander, StoragePathSettings)) _ = RefreshCacheUsageAsync();
                if (ReferenceEquals(expander, MemorySettings)) RefreshMemoryUsage();
                expander.Expanded -= SettingsExpander_OnExpanded;
                expander.Expanded += SettingsExpander_OnExpanded;
                UseNonVirtualizingLayout(expander);
            }
        });
    }

    private void SettingsExpander_OnExpanded(object sender, EventArgs e)
    {
        UseNonVirtualizingLayout((DependencyObject)sender);
    }

    private static void UseNonVirtualizingLayout(DependencyObject root)
    {
        foreach (var repeater in root.FindDescendants().OfType<ItemsRepeater>())
        {
            if (repeater.Layout is StackLayout stackLayout)
                repeater.Layout = new NonVirtualizingStackLayout { Spacing = stackLayout.Spacing };
        }
    }

    private static List<FontInfo> GetAllFonts()
    {
        var names = CanvasTextFormat.GetSystemFontFamilies();
        var displayNames = CanvasTextFormat.GetSystemFontFamilies(localeList);
        var models = new List<FontInfo>();
        for (var i = 0; i < names.Length; i++)
        {
            models.Add(new FontInfo
            {
                Name = displayNames[i],
                Value = names[i]
            });
        }

        return [.. models.OrderBy(t => t.Name)];
    }
    [GeneratedBindableCustomProperty]
    public partial class FontInfo
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    private async Task GetRomaji()
    {
        RomajiStatus.Header = "正在下载资源文件 请稍等";
        try
        {
            var undeletedRomajiFile = await ApplicationData.Current.LocalCacheFolder.TryGetItemAsync("RomajiData.zip");
            if (undeletedRomajiFile != null) await undeletedRomajiFile.DeleteAsync();
        }
        catch
        {
            // ignored
        }

        var sf = await ApplicationData.Current.LocalCacheFolder.CreateFileAsync("RomajiData.zip");
        var downloader = new BackgroundDownloader();
        var dl = downloader.CreateDownload(new Uri("https://api.kengwang.com.cn/hyplayer/getromaji.php"),
            sf);
        _ = HandleDownloadAsync(dl);
    }

    private async Task HandleDownloadAsync(DownloadOperation dl)
    {
        var process = new Progress<DownloadOperation>(ProgressCallback);
        try
        {
            await dl.StartAsync().AsTask(process);
            if (dl.Progress.TotalBytesToReceive > 5000) _ = OnRomajiDownloadDone(dl);
        }
        catch (Exception e)
        {
            RomajiStatus.Header = "下载错误 " + e.Message;
        }
    }

    private void ProgressCallback(DownloadOperation obj)
    {
        if (obj.Progress.TotalBytesToReceive == 0)
        {
            RomajiStatus.Header = "下载错误 " + obj.CurrentWebErrorStatus;
            return;
        }

        RomajiStatus.Header = $"正在下载资源文件 ({obj.Progress.BytesReceived * 100 / obj.Progress.TotalBytesToReceive:D}%)";
    }

    private async Task OnRomajiDownloadDone(DownloadOperation obj)
    {
        try
        {
            //下载完成
            //unzip
            RomajiStatus.Header = "正在解压,请稍等......";
            var path =
                (await ApplicationData.Current.LocalFolder.CreateFolderAsync("Romaji",
                    CreationCollisionOption.OpenIfExists)).Path;
            //Read the file stream
            var a = await obj.ResultFile.OpenStreamForReadAsync();

            //unzip
            var archive = new ZipArchive(a);
            archive.ExtractToDirectory(path);
            _ = obj.ResultFile.DeleteAsync();

            Ioc.Default.GetRequiredService<IUIStateService>().KawazuConv = new KawazuConverter(path);
        }
        catch (Exception e)
        {
            RomajiStatus.Header = "罗马字文件解压错误: " + e.Message;
        }
        finally
        {
            RomajiStatus.Header =
                (Ioc.Default.GetRequiredService<IUIStateService>().KawazuConv == null ? "请重新下载资源文件" : "可以转换");
            ButtonDownloadRomaji.Visibility = Ioc.Default.GetRequiredService<IUIStateService>().KawazuConv != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static readonly string[] localeList = ["zh-cn"];

    private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
    {
        _ = GetRomaji();
    }

    private void ButtonXREALIPSave_OnClick(object sender, RoutedEventArgs e)
    {
        ApplicationData.Current.LocalSettings.Values["xRealIp"] =
            TextBoxXREALIP.Text == "" ? null : TextBoxXREALIP.Text;
        _api?.Option.XRealIP = (string)ApplicationData.Current.LocalSettings.Values["xRealIp"];
    }

    private async void ButtonDownloadSelect_OnClick(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder == null) return;
        if (!await OfferPathMigrationAsync("下载的歌曲", _setting.downloadDir, folder, StorageContentKind.Music)) return;
        StorageApplicationPermissions.FutureAccessList.AddOrReplace("downloadFolder", folder);
        _setting.downloadDir = folder.Path;
    }

    private async void ButtonSearchingSelect_OnClick(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder == null) return;
        if (!await OfferPathMigrationAsync("本地音乐", _setting.searchingDir, folder, StorageContentKind.Music)) return;
        StorageApplicationPermissions.FutureAccessList.AddOrReplace("searchingFolder", folder);
        _setting.searchingDir = folder.Path;
    }


    private void UIElement_OnTapped(object sender, TappedRoutedEventArgs tappedRoutedEventArgs)
    {
        if (_elapse-- <= 0) _navigation.Navigate(typeof(TestPage));
    }


    private void ControlSoundChecked(object sender, RoutedEventArgs e)
    {
        if (isbyprogram) return;
        _setting.uiSound = true;
        ElementSoundPlayer.State = ElementSoundPlayerState.On;
        ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.On;
    }

    private void ControlSoundUnChecked(object sender, RoutedEventArgs e)
    {
        if (isbyprogram) return;
        _setting.uiSound = false;
        ElementSoundPlayer.State = ElementSoundPlayerState.Off;
        ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.Off;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _ = HistoryManagement.ClearHistory();
    }


    private void CopyDeviceCode_Click(object sender, RoutedEventArgs e)
    {
        var deviceInfo = new EasClientDeviceInformation();
        var dp = new DataPackage();
        dp.SetText(deviceInfo.Id.ToString());
        Clipboard.SetContent(dp);
    }

    private void NBShadowDepth_OnValueChanged(object o, RangeBaseValueChangedEventArgs rangeBaseValueChangedEventArgs)
    {
        if (isbyprogram) return;
        var size = (int)SliderAlbumShadowDepth.Value;
        _setting.expandedCoverShadowDepth = Math.Max(0, size);
    }


    private static ModelEntry? ModelOf(object sender) => sender is FrameworkElement element ? element.Tag as ModelEntry ?? element.DataContext as ModelEntry : null;

    private async void ModelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is { } entry) await Models.DownloadAsync(entry);
    }

    private void ModelCancel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is { } entry) Models.CancelDownload(entry);
    }

    private async void ModelDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is { } entry) await Models.DeleteAsync(entry);
    }

    private async void ModelImport_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } entry) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".onnx");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var error = await Models.ImportAsync(entry, file);
        if (error is not null) _notification.ShowMessage("导入模型失败", error);
    }

    private async void ModelFolderSelect_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ChangeModelFolderAsync(folder);
    }

    private async void GoToStoragePathSettings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SettingsPivot.Items)
        {
            if (item is not PivotItem { Header: "存储设置" } storage) continue;
            SettingsPivot.SelectedItem = storage;
            break;
        }
        for (var attempt = 0; attempt < 20 && StoragePathSettings is null; attempt++)
            await Task.Delay(50);
        if (StoragePathSettings is null) return;
        await Task.Delay(100);
        StoragePathSettings.IsExpanded = true;
        StoragePathSettings.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
    }

    private async void ModelFolderReset_Click(object sender, RoutedEventArgs e)
    {
        await ChangeModelFolderAsync(null);
    }

    private async Task ChangeModelFolderAsync(StorageFolder? target)
    {
        StorageFolder current;
        StorageFolder destination;
        try
        {
            current = await Models.GetFolderAsync();
            destination = target ?? await ModelManager.GetDefaultFolderAsync();
        }
        catch (Exception ex)
        {
            _notification.ShowMessage("无法更改模型位置", ex.Message);
            return;
        }
        if (string.Equals(current.Path, destination.Path, StringComparison.OrdinalIgnoreCase))
        {
            await Models.SetFolderAsync(target);
            return;
        }

        var files = await ModelManager.GetModelFilesAsync(current);
        if (files.Count > 0)
        {
            ulong totalBytes = 0;
            foreach (var file in files) totalBytes += (await file.GetBasicPropertiesAsync()).Size;
            var (proceed, migrate, deleteOriginal) = await AskMigrationAsync("分析模型", files.Count, totalBytes, current.Path, destination.Path);
            if (!proceed) return;
            if (migrate)
            {
                _notification.ShowMessage("正在迁移分析模型", "请稍候，迁移完成前不要关闭应用");
                try
                {
                    await Models.MigrateAsync(current, destination, deleteOriginal);
                }
                catch (Exception ex)
                {
                    _notification.ShowMessage("迁移失败", $"模型位置未更改：{ex.Message}");
                    return;
                }
                _notification.ShowMessage("迁移完成", deleteOriginal ? "已迁移并删除原位置的模型文件" : "已迁移，原位置的模型文件已保留");
            }
        }
        await Models.SetFolderAsync(target);
    }

    private async void ButtonCacheSelect_OnClick(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder == null) return;
        if (!await OfferPathMigrationAsync("缓存", _setting.cacheDir, folder, StorageContentKind.Cache)) return;
        StorageApplicationPermissions.FutureAccessList.AddOrReplace("cacheFolder", folder);
        _setting.cacheDir = folder.Path;
        SimpleCacher.Reset();
        _ = RefreshCacheUsageAsync();
    }

    private async void ButtonDownloadReset_OnClick(object sender, RoutedEventArgs e)
    {
        StorageFolder target;
        try
        {
            target = await StorageFolder.GetFolderFromPathAsync(Setting.DefaultDownloadDir);
        }
        catch (Exception ex)
        {
            _notification.ShowMessage("无法恢复默认下载路径", ex.Message);
            return;
        }
        if (!await OfferPathMigrationAsync("下载的歌曲", _setting.downloadDir, target, StorageContentKind.Music)) return;
        _setting.downloadDir = null;
    }

    private async void ButtonSearchingReset_OnClick(object sender, RoutedEventArgs e)
    {
        StorageFolder target;
        try
        {
            target = await StorageFolder.GetFolderFromPathAsync(_setting.downloadDir);
        }
        catch (Exception ex)
        {
            _notification.ShowMessage("无法恢复默认搜索路径", ex.Message);
            return;
        }
        if (!await OfferPathMigrationAsync("本地音乐", _setting.searchingDir, target, StorageContentKind.Music)) return;
        _setting.searchingDir = null;
    }

    private async void ButtonCacheReset_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await OfferPathMigrationAsync("缓存", _setting.cacheDir, ApplicationData.Current.LocalCacheFolder, StorageContentKind.Cache)) return;
        _setting.cacheDir = null;
        SimpleCacher.Reset();
        _ = RefreshCacheUsageAsync();
    }

    private async Task<(bool Proceed, bool Migrate, bool DeleteOriginal)> AskMigrationAsync(string what, int count, ulong totalBytes, string from, string to)
    {
        var migrateDialog = new ContentDialog
        {
            Title = $"迁移{what}",
            Content = $"原位置有 {count} 个{what}文件（共 {StorageMaintenance.FormatSize(totalBytes)}），是否迁移到新位置？\n{from}\n→ {to}",
            PrimaryButtonText = "迁移",
            SecondaryButtonText = "不迁移",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var choice = await migrateDialog.ShowAsync();
        if (choice == ContentDialogResult.None) return (false, false, false);
        if (choice == ContentDialogResult.Secondary) return (true, false, false);
        var deleteDialog = new ContentDialog
        {
            Title = "删除原位置的文件",
            Content = $"迁移完成后，是否删除原位置的{what}文件？",
            PrimaryButtonText = "删除",
            CloseButtonText = "保留",
            DefaultButton = ContentDialogButton.Close
        };
        return (true, true, await deleteDialog.ShowAsync() == ContentDialogResult.Primary);
    }

    private async Task<bool> OfferPathMigrationAsync(string what, string oldPath, StorageFolder destination, StorageContentKind kind)
    {
        if (string.IsNullOrEmpty(oldPath) || StorageMaintenance.IsSamePath(oldPath, destination.Path)) return true;
        StorageFolder source;
        List<StorageItemInfo> items;
        try
        {
            source = await StorageFolder.GetFolderFromPathAsync(oldPath);
            items = await StorageMaintenance.CollectAsync(source, kind, destination.Path);
        }
        catch
        {
            return true;
        }
        if (items.Count == 0) return true;

        ulong totalBytes = 0;
        foreach (var item in items) totalBytes += item.Size;
        var (proceed, migrate, deleteOriginal) = await AskMigrationAsync(what, items.Count, totalBytes, source.Path, destination.Path);
        if (!proceed) return false;
        if (!migrate) return true;

        _notification.ShowMessage($"正在迁移{what}", $"共 {items.Count} 个文件，迁移完成前不要关闭应用");
        var result = await StorageMaintenance.MigrateAsync(source, destination, items, deleteOriginal);
        var detail = $"已迁移 {result.Copied} 个文件";
        if (result.Skipped > 0) detail += $"，{result.Skipped} 个因新位置已有同名文件而跳过";
        if (result.Failed > 0) detail += $"，{result.Failed} 个失败";
        if (deleteOriginal) detail += "，原位置的文件已删除";
        _notification.ShowMessage(result.Failed > 0 ? $"{what}迁移部分完成" : $"{what}迁移完成", detail);
        return true;
    }

    private void RefreshMemoryUsage(string? prefix = null)
    {
        var usage = $"当前占用 {StorageMaintenance.FormatSize(MemoryGovernor.WorkingSetBytes)}";
        MemoryNowCard.Description = prefix is null ? usage : $"{prefix}，{usage}";
    }

    private async void ReclaimMemory_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        var freed = await MemoryGovernor.ReclaimAsync();
        RefreshMemoryUsage($"已释放 {StorageMaintenance.FormatSize(freed)}");
        button.IsEnabled = true;
    }

    private async Task RefreshCacheUsageAsync()
    {
        try
        {
            var used = await StorageMaintenance.GetCacheUsageAsync();
            CacheLimitCard.Description = $"当前已使用 {StorageMaintenance.FormatSize(used)}";
        }
        catch
        {
        }
    }
    private void StackPanel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _elapse -= 2;
        if (_elapse <= 0) ApplicationData.Current.RoamingSettings.Values["CanDownload"] = true;
    }

    private void DeviceInfo_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DeviceInfo.ContextFlyout.ShowAt(DeviceInfo);
    }

    private async void RestartBtn_Click(object sender, RoutedEventArgs e)
    {
        await CoreApplication.RequestRestartAsync("ChangeThemeRestart");
    }

    private void BtnXboxReserve_Click(object sender, RoutedEventArgs e)
    {
        _navigation.CollectGarbage();
    }

    private async void HotLyricOnStartUp_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            var uri = new Uri($"hot-lyric:///?from={Package.Current.Id.FamilyName}");
            if (await Launcher.QueryUriSupportAsync(uri, LaunchQuerySupportType.Uri) !=
                LaunchQuerySupportStatus.Available)
            {
                var dlg = new ContentDialog
                {
                    Title = "当前未安装 「热词」",
                    Content = "是否前往商店安装 「热词」",
                    CloseButtonText = "否",
                    PrimaryButtonText = "安装「热词」"
                };

                var res = await dlg.ShowAsync(ContentDialogPlacement.Popup);
                if (res == ContentDialogResult.Primary)
                {
                    await Launcher.LaunchUriAsync(new Uri("ms-windows-store://pdp?productId=9MXFFHVQVBV9"));
                    return;
                }

                _setting.hotlyricOnStartup = false;
            }
            else
            {
                await Launcher.LaunchUriAsync(uri);
            }
        }
        catch
        {
        }
    }

    private async void BtnChangeAudioRenderDevice_Click(object sender, RoutedEventArgs e)
    {
        var devicePicker = new DevicePicker();
        devicePicker.Filter.SupportedDeviceClasses.Add(DeviceClass.AudioRender);
        var ge = BtnChangeAudioRenderDevice.TransformToVisual(null);
        var point = ge.TransformPoint(new Point());
        var rect = new Rect(point,
            new Point(point.X + BtnChangeAudioRenderDevice.ActualWidth,
                point.Y + BtnChangeAudioRenderDevice.ActualHeight));
        var device = await devicePicker.PickSingleDeviceAsync(rect);
        if (device != null) _setting.AudioRenderDevice = device.Id;
    }

    private void BtnChangeToDefaultAudioRenderDevice_Click(object sender, RoutedEventArgs e)
    {
        _setting.AudioRenderDevice = "";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await UpdateManager.PopupVersionCheck();
    }

    private void ComboBoxSongBr_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isbyprogram) return;
        var selectedItem = sender?.As<ComboBox>().SelectedItem?.As<ComboBoxItem>();
        _setting.audioRate = selectedItem.Tag.ToString();
    }

    private void ComboBoxSongDownloadBr_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isbyprogram) return;
        var selectedItem = sender?.As<ComboBox>().SelectedItem?.As<ComboBoxItem>();
        _setting.downloadAudioRate = selectedItem.Tag.ToString();
    }

    private void CheckCanaryChannelButton_Click(object sender, RoutedEventArgs e)
    {
        _ = UpdateManager.GetUserCanaryChannelAvailability(canaryEmail.Text);
    }

    private async void ClearTileCache_Click(object sender, RoutedEventArgs e)
    {
        var storageFolder = await ApplicationData.Current.TemporaryFolder.TryGetItemAsync("LocalTileBackground");
        if (storageFolder != null) await storageFolder.DeleteAsync();
    }


    private void ResetPureLyricIdleColor(object sender, RoutedEventArgs e)
    {
        _setting.pureLyricIdleColor = null;
    }

    private void ConfirmPureLyricIdleColor(object sender, RoutedEventArgs e)
    {
        _setting.pureLyricIdleColor = PureLyricIdle.SelectedColor;
    }

    private void ResetPureLyricFocusingColor(object sender, RoutedEventArgs e)
    {
        _setting.pureLyricFocusingColor = null;
    }

    private void ConfirmPureLyricFocusingColor(object sender, RoutedEventArgs e)
    {
        _setting.pureLyricFocusingColor = PureLyricFocusing.SelectedColor;
    }

    private void ResetKaraokLyricFocusingColor(object sender, RoutedEventArgs e)
    {
        _setting.karaokLyricFocusingColor = null;
    }

    private void ConfirmKaraokLyricFocusingColor(object sender, RoutedEventArgs e)
    {
        _setting.karaokLyricFocusingColor = KaraokLyricFocusing.SelectedColor;
    }

    private void ApplyNewAcrylic()
    {
        var Brush = new Microsoft.UI.Xaml.Media.AcrylicBrush()
        {
            BackgroundSource = AcrylicBackgroundSource.HostBackdrop,
            TintColor = (Windows.UI.Color)Application.Current.Resources["SystemRevealAltHighColor"],
            TintOpacity = TintOpacitySlider.Value,
            TintLuminosityOpacity = TintOpacityLuminositySlider.Value,
            FallbackColor = (Windows.UI.Color)Application.Current.Resources["SystemRevealAltHighColor"],
        };
        PreviewAcrylic.Fill = Brush;
    }

    private void TintOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ApplyNewAcrylic();
    }

    private void TintOpacityLuminosity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ApplyNewAcrylic();
    }

    private async void AboutRomaji_Click(object sender, RoutedEventArgs e)
    {
        await AboutRomajiDialog.ShowAsync();
    }

    private void DisplayMaintain_OnChecked(object sender, RoutedEventArgs e)
    {
        Ioc.Default.GetRequiredService<IUIStateService>().DisplayRequest.RequestActive();
    }

    private void DisplayMaintain_OnUnchecked(object sender, RoutedEventArgs e)
    {
        Ioc.Default.GetRequiredService<IUIStateService>().DisplayRequest.RequestRelease();
    }

    private async void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        await SimpleCacher.ClearAllCacheAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(_setting!.cacheDir);
        var files = await folder.GetFilesAsync();
        foreach (var file in files)
        {
            if (file.FileType == ".flac" || file.FileType == ".mp3")
            {
                await file.DeleteAsync();
            }
        }
        await RefreshCacheUsageAsync();
    }

    private void LogoffLastFMAccount_Click(object sender, RoutedEventArgs e)
    {
        _setting.LastFMSession = null;
    }

    private void LoginLastFMAccount_Click(object sender, RoutedEventArgs e)
    {
        _ = Launcher.LaunchUriAsync(new Uri($"https://www.last.fm/api/auth?api_key={Ioc.Default.GetRequiredService<LastFMClient>().Options.ApiKey}&cb=hyplayer://link.last.fm"));
    }
}