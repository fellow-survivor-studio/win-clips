using System.Buffers.Binary;
using System.Text;

namespace ClipboardHistory.Core;

/// <summary>Byte-level helpers for clipboard formats (no Win32 calls, so they are unit-testable).</summary>
public static class ClipboardData
{
    /// <summary>CF_UNICODETEXT payload: UTF-16LE with a terminating NUL.</summary>
    public static byte[] TextBytes(string text) => Encoding.Unicode.GetBytes(text + "\0");

    /// <summary>CF_HDROP payload: a DROPFILES header followed by a double-NUL-terminated wide path list.</summary>
    public static byte[] DropFilesBytes(IEnumerable<string> paths)
    {
        var body = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var data = new byte[20 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 20); // pFiles: offset of the list
        // pt.x, pt.y, fNC stay 0
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1); // fWide: paths are UTF-16
        body.CopyTo(data, 20);
        return data;
    }

    /// <summary>Width and height of a CF_DIB payload, or null if the header is not a bitmap header.</summary>
    public static (int Width, int Height)? DibSize(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var w = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        if (w <= 0 || h == 0) return null;
        return (w, Math.Abs(h));
    }

    /// <summary>
    /// Wrap a CF_DIB payload in a BITMAPFILEHEADER so standard decoders can read it as a .bmp.
    /// Returns null if the DIB header is not usable.
    /// </summary>
    public static byte[]? DibToBmp(byte[] dib)
    {
        if (DibSize(dib) is null) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));

        long extra = 0;
        const uint BI_BITFIELDS = 3, BI_ALPHABITFIELDS = 6;
        if (headerSize == 40 && compression == BI_BITFIELDS) extra = 12;       // three colour masks
        if (headerSize == 40 && compression == BI_ALPHABITFIELDS) extra = 16;  // four colour masks
        long colors = colorsUsed != 0 ? colorsUsed : bitCount is > 0 and <= 8 ? 1L << bitCount : 0;
        long offset = 14 + headerSize + extra + colors * 4;
        if (offset > 14 + (long)dib.Length) return null;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)offset);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    /// <summary>One-line text for the history list (the list shows the kind as an icon).</summary>
    public static string ListPreview(HistoryEntry entry, int maxChars = 140)
    {
        switch (entry.Kind)
        {
            case EntryKind.Image:
                return entry.Text; // e.g. "[Image 1920x1080]"
            case EntryKind.Files:
                var names = entry.Text.Split('\n').Select(p =>
                {
                    var name = Path.GetFileName(p.TrimEnd('\\', '/'));
                    return string.IsNullOrEmpty(name) ? p : name;
                }).ToList();
                var joined = string.Join(", ", names);
                return names.Count > 1 ? $"{names.Count}개 파일: {joined}" : joined;
            default:
                var single = string.Join(" ", entry.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return single.Length > maxChars ? single[..maxChars] + "…" : single;
        }
    }
}
