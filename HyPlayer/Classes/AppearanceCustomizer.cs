#nullable enable
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace HyPlayer.Classes;

internal static class AppearanceCustomizer
{
    private const string FontResourceKey = "ContentControlThemeFontFamily";
    private static object? _defaultFont;

    public static void ApplyFont()
    {
        var name = Ioc.Default.GetRequiredService<Setting>().customUiFontFamily;
        var resources = Application.Current.Resources;
        if (_defaultFont == null && resources.TryGetValue(FontResourceKey, out var original)) _defaultFont = original;
        if (string.IsNullOrWhiteSpace(name))
        {
            if (_defaultFont != null) resources[FontResourceKey] = _defaultFont;
            else resources.Remove(FontResourceKey);
            (Window.Current?.Content as Control)?.ClearValue(Control.FontFamilyProperty);
            return;
        }

        var font = new FontFamily(name);
        resources[FontResourceKey] = font;
        if (Window.Current?.Content is Control root) root.FontFamily = font;
    }

    public static void ApplyBackground(Image image, Windows.UI.Xaml.Shapes.Rectangle blur, CommunityToolkit.WinUI.Media.BackdropBlurBrush blurBrush)
    {
        var setting = Ioc.Default.GetRequiredService<Setting>();
        var file = setting.backgroundImagePath;
        if (string.IsNullOrEmpty(file))
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
            blur.Visibility = Visibility.Collapsed;
            return;
        }

        if ((image.Tag as string) != file || image.Source == null)
        {
            image.Tag = file;
            image.Source = new BitmapImage(new Uri("ms-appdata:///local/" + file))
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 1920
            };
        }

        image.Opacity = Math.Clamp(setting.backgroundImageOpacity, 0, 100) / 100;
        image.Stretch = setting.backgroundImageStretch switch
        {
            0 => Stretch.UniformToFill,
            1 => Stretch.Uniform,
            2 => Stretch.Fill,
            _ => Stretch.None
        };
        image.Visibility = Visibility.Visible;
        blurBrush.Amount = Math.Clamp(setting.backgroundImageBlur, 0, 60);
        blur.Visibility = setting.backgroundImageBlur > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static async Task<bool> PickBackgroundAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" }) picker.FileTypeFilter.Add(ext);
        var picked = await picker.PickSingleFileAsync();
        if (picked == null) return false;

        var name = $"ui_background_{DateTime.Now.Ticks}{picked.FileType.ToLowerInvariant()}";
        await picked.CopyAsync(ApplicationData.Current.LocalFolder, name, NameCollisionOption.ReplaceExisting);
        var setting = Ioc.Default.GetRequiredService<Setting>();
        var previous = setting.backgroundImagePath;
        setting.backgroundImagePath = name;
        await DeleteLocalFileAsync(previous);
        return true;
    }

    public static async Task RemoveBackgroundAsync()
    {
        var setting = Ioc.Default.GetRequiredService<Setting>();
        var previous = setting.backgroundImagePath;
        setting.backgroundImagePath = string.Empty;
        await DeleteLocalFileAsync(previous);
    }

    private static async Task DeleteLocalFileAsync(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(name) is StorageFile old)
                await old.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
        }
    }
}
