using System.Text;
using ClipboardHistory.Core;

namespace ClipboardHistory.Tests;

public class ClipboardDataTests
{
    [Fact]
    public void Text_bytes_are_utf16_with_nul()
    {
        Assert.Equal(new byte[] { 0x41, 0, 0x5C, 0xD5, 0, 0 }, ClipboardData.TextBytes("A한"));
    }

    [Fact]
    public void Dropfiles_layout_matches_win32()
    {
        var data = ClipboardData.DropFilesBytes(new[] { @"C:\a", @"D:\b" });
        Assert.Equal(20u, BitConverter.ToUInt32(data, 0));  // pFiles
        Assert.Equal(1u, BitConverter.ToUInt32(data, 16));  // fWide
        Assert.Equal("C:\\a\0D:\\b\0\0", Encoding.Unicode.GetString(data, 20, data.Length - 20));
    }

    private static byte[] Dib(int headerSize, int w, int h, ushort bpp, uint compression = 0, uint clrUsed = 0, int pixelBytes = 16)
    {
        var dib = new byte[headerSize + pixelBytes + 64];
        BitConverter.GetBytes(headerSize).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes(bpp).CopyTo(dib, 14);
        BitConverter.GetBytes(compression).CopyTo(dib, 16);
        BitConverter.GetBytes(clrUsed).CopyTo(dib, 32);
        return dib;
    }

    [Fact]
    public void Dib_size_handles_top_down_and_garbage()
    {
        Assert.Equal((10, 20), ClipboardData.DibSize(Dib(40, 10, -20, 32)));
        Assert.Null(ClipboardData.DibSize(new byte[10]));
        Assert.Null(ClipboardData.DibSize(Dib(12, 10, 10, 24))); // old BITMAPCOREHEADER: not supported
        Assert.Null(ClipboardData.DibSize(Dib(40, 0, 10, 24)));
    }

    [Theory]
    [InlineData(40, 24, 0u, 0u, 54)]          // plain 24-bit
    [InlineData(40, 32, 3u, 0u, 66)]          // BI_BITFIELDS: 3 masks follow the header
    [InlineData(124, 32, 3u, 0u, 138)]        // BITMAPV5HEADER includes the masks
    [InlineData(40, 8, 0u, 0u, 54 + 256 * 4)] // 8-bit palette
    [InlineData(40, 8, 0u, 16u, 54 + 16 * 4)] // palette with biClrUsed
    public void Bmp_wrapper_points_at_pixels(int headerSize, int bpp, uint compression, uint clrUsed, int expectedOffset)
    {
        var dib = Dib(headerSize, 2, 2, (ushort)bpp, compression, clrUsed, pixelBytes: 2048);
        var bmp = ClipboardData.DibToBmp(dib)!;
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal((uint)bmp.Length, BitConverter.ToUInt32(bmp, 2));
        Assert.Equal((uint)expectedOffset, BitConverter.ToUInt32(bmp, 10));
        Assert.Equal(dib, bmp[14..]);
    }

    [Fact]
    public void List_preview()
    {
        Assert.Equal("a b c", ClipboardData.ListPreview(new HistoryEntry(1, EntryKind.Text, "  a\n b\t\tc ", false)));
        Assert.Equal(new string('x', 5) + "…", ClipboardData.ListPreview(new HistoryEntry(1, EntryKind.Text, new string('x', 9), false), 5));
        Assert.Equal("one.txt", ClipboardData.ListPreview(new HistoryEntry(1, EntryKind.Files, @"C:\dir\one.txt", false)));
        Assert.Equal("2개 파일: one.txt, dir", ClipboardData.ListPreview(new HistoryEntry(1, EntryKind.Files, "C:\\x\\one.txt\nC:\\dir\\", false)));
        Assert.Equal("[Image 4x3]", ClipboardData.ListPreview(new HistoryEntry(1, EntryKind.Image, "[Image 4x3]", false)));
    }
}
