#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using HyPlayer.Classes.Settings;
using HyPlayer.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace HyPlayer.Classes;

internal static class FolderAccessGuard
{
    public static async Task<bool> CanWriteAsync(StorageFolder folder)
    {
        try
        {
            var probe = await folder.CreateFileAsync($".hyplayer_access_{Guid.NewGuid():N}.tmp", CreationCollisionOption.GenerateUniqueName);
            await probe.DeleteAsync(StorageDeleteOption.PermanentDelete);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> EnsureWritableAsync(StorageFolder folder, string what)
    {
        if (await CanWriteAsync(folder)) return true;
        var dialog = new ContentDialog
        {
            Title = "无法写入所选文件夹",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"HyPlayer 没有「{folder.Path}」的写入权限，{what}将无法保存到这里。\n\n" +
                       "可以换一个文件夹，或者在 Windows 设置里允许 HyPlayer 访问文件系统后再选一次。"
            },
            PrimaryButtonText = "打开权限设置",
            SecondaryButtonText = "重新选择",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await OpenPrivacySettingsAsync();
        return false;
    }

    public static Task OpenPrivacySettingsAsync() =>
        Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-broadfilesystemaccess")).AsTask();

    public static async Task CheckSavedFoldersAsync()
    {
        var setting = Ioc.Default.GetRequiredService<Setting>();
        var lost = new List<string>();
        Check("缓存目录", nameof(PlaybackSettings.cacheDir), setting.cacheDir, lost);
        Check("下载目录", nameof(Setting.downloadDir), setting.downloadDir, lost);
        Check("本地音乐搜索目录", nameof(Setting.searchingDir), setting.searchingDir, lost);
        var models = Ioc.Default.GetRequiredService<HyPlayer.Services.Playback.Models.ModelManager>();
        if (models.IsCustomFolder && !models.IsCustomFolderAccessible) lost.Add($"• 分析模型目录：{models.FolderPath}");
        if (lost.Count == 0) return;

        await Task.Delay(TimeSpan.FromSeconds(3));
        var dialog = new ContentDialog
        {
            Title = "部分文件夹无法访问",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "以下自定义文件夹已失去访问权限，暂时改用默认位置：\n\n" + string.Join("\n", lost) +
                       "\n\n在 设置 → 存储设置 中重新选择一次这些文件夹即可恢复。"
            },
            PrimaryButtonText = "去设置",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary
        };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    Ioc.Default.GetRequiredService<INavigationService>().Navigate(typeof(Pages.Settings));
                return;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static void Check(string name, string key, string effective, List<string> lost)
    {
        var stored = SettingsBase.GetSettings<string?>(key, null);
        if (string.IsNullOrEmpty(stored) || StorageMaintenance.IsSamePath(stored, effective)) return;
        lost.Add($"• {name}：{stored}");
    }
}
