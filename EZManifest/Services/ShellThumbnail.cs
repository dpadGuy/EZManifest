using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace EZManifest.Services;

internal static class ShellThumbnail
{
    public static async Task<BitmapImage?> GetAsync(string path, bool isDirectory, uint size = 64)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            StorageItemThumbnail? thumb = isDirectory
                ? await (await StorageFolder.GetFolderFromPathAsync(path))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.UseCurrentScale)
                : await (await StorageFile.GetFileFromPathAsync(path))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.UseCurrentScale);

            if (thumb is null)
                return null;

            var image = new BitmapImage();
            await image.SetSourceAsync(thumb);
            thumb.Dispose();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
