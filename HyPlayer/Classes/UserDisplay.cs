using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace HyPlayer.Classes
{
    public class UserDisplay
    {
        private NCUser user;
        public UserDisplay(NCUser user)
        {
            this.user = user;
        }
        public string UserName => user.Name;
        public string Signature => user.Signature;
        private Uri avatarUri => Ioc.Default.GetRequiredService<Setting>().noImage ? new("ms-appx:///Assets/icon.png") : new(SizedAvatar, UriKind.RelativeOrAbsolute);

        private string SizedAvatar =>
            user.Avatar is not null && user.Avatar.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !user.Avatar.Contains('?')
                ? user.Avatar + "?param=" + StaticSource.PICSIZE_COMMENTUSER_AVATAR
                : user.Avatar;
        public ImageSource AvatarSource => new BitmapImage(avatarUri);
    }
}
