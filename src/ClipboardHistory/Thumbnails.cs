using System;
using System.IO;
using System.Windows.Media.Imaging;
using ClipboardHistory.Core;
using ClipboardHistory.Services;

namespace ClipboardHistory;

/// <summary>
/// Small list thumbnails for image entries. The first time an image is shown it is decoded
/// from the stored bitmap and saved as a small PNG next to it, so later opens are fast.
/// Safe to call from a background thread (touches files only, not the database).
/// </summary>
internal static class Thumbnails
{
    // Room for the list cell (about 160 x 52 at up to 200% scaling).
    private const int MaxWidthPx = 320;
    private const int MaxHeightPx = 104;

    public static BitmapSource? Load(HistoryStore store, string blob)
    {
        var path = store.ThumbnailPath(blob);
        try
        {
            if (File.Exists(path))
                return FromFile(path);

            var dib = store.ReadImageBlob(blob);
            if (dib is null) return null;
            var bmp = ClipboardData.DibToBmp(dib);
            var size = ClipboardData.DibSize(dib);
            if (bmp is null || size is not { } s) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bmp);
            // Scale by whichever side hits the cell limit first; never enlarge.
            if ((double)s.Width / s.Height > (double)MaxWidthPx / MaxHeightPx)
            {
                if (s.Width > MaxWidthPx) image.DecodePixelWidth = MaxWidthPx;
            }
            else if (s.Height > MaxHeightPx)
            {
                image.DecodePixelHeight = MaxHeightPx;
            }
            image.EndInit();
            image.Freeze();

            Save(image, path);
            // The entry may have been deleted meanwhile; don't leave an orphan thumbnail.
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(blob))))
                TryDelete(path);
            return image;
        }
        catch (Exception ex)
        {
            Log.Error($"could not make thumbnail for {blob}", ex);
            TryDelete(path); // a broken cache file would fail every time
            return null;
        }
    }

    private static BitmapSource FromFile(string path)
    {
        using var fs = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = fs;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            encoder.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
