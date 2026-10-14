using System.IO;
using System.Windows.Media.Imaging;

namespace MiningAssistant.App.Services;

internal static class Imaging
{
    public static BitmapSource FromPng(byte[] png)
    {
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    public static BitmapSource FromBitmap(System.Drawing.Bitmap bmp) =>
        FromPng(Vision.Imaging.ImageOps.ToPng(bmp));
}
