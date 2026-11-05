using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static ClipboardHistory.Interop.NativeMethods;

namespace ClipboardHistory.Interop;

internal abstract record ClipboardContent;
internal sealed record TextContent(string Text) : ClipboardContent;
internal sealed record FilesContent(IReadOnlyList<string> Paths) : ClipboardContent;
internal sealed record ImageContent(byte[] Dib) : ClipboardContent;

/// <summary>Direct Win32 clipboard access (raw formats, one open/close per read).</summary>
internal static class Win32Clipboard
{
    // Formats apps set to ask clipboard managers not to record the copy (password managers etc.).
    private static readonly uint ExcludeFromMonitor = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessor");
    private static readonly uint ViewerIgnore = RegisterClipboardFormatW("Clipboard Viewer Ignore");
    private static readonly uint CanIncludeInHistory = RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    private static bool Open(IntPtr owner, int retries = 10)
    {
        // Another app may hold the clipboard open briefly; retry a few times.
        for (int i = 0; i < retries; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// Read the most relevant clipboard content. Priority: files > text > image
    /// (an image copy often carries a text fallback, a text copy rarely carries an image).
    /// Returns null when empty, unsupported, or marked as private by the source app.
    /// </summary>
    public static ClipboardContent? Read(IntPtr owner, int maxImageBytes)
    {
        if (!Open(owner)) return null;
        try
        {
            if (IsPrivate()) return null;
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var files = ReadFileList();
                if (files.Count > 0) return new FilesContent(files);
            }
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadText();
                if (text is not null) return new TextContent(text);
            }
            if (IsClipboardFormatAvailable(CF_DIB))
            {
                var dib = ReadGlobal(CF_DIB, maxImageBytes);
                if (dib is not null) return new ImageContent(dib);
            }
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool IsPrivate()
    {
        if (ExcludeFromMonitor != 0 && IsClipboardFormatAvailable(ExcludeFromMonitor)) return true;
        if (ViewerIgnore != 0 && IsClipboardFormatAvailable(ViewerIgnore)) return true;
        if (CanIncludeInHistory != 0 && IsClipboardFormatAvailable(CanIncludeInHistory))
        {
            // A DWORD 0 means "do not keep this in clipboard history".
            var value = ReadGlobal(CanIncludeInHistory, 64);
            if (value is { Length: >= 4 } && BitConverter.ToInt32(value, 0) == 0) return true;
        }
        return false;
    }

    private static byte[]? ReadGlobal(uint format, int limit)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (long)(ulong)GlobalSize(handle);
        if (size <= 0 || size > limit) return null;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, (int)size);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var size = (long)(ulong)GlobalSize(handle);
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            // Stop at the first NUL, but never read past the allocation.
            int maxChars = (int)Math.Min(size / 2, int.MaxValue);
            int len = 0;
            while (len < maxChars && Marshal.ReadInt16(ptr, len * 2) != 0) len++;
            return Marshal.PtrToStringUni(ptr, len);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static List<string> ReadFileList()
    {
        var paths = new List<string>();
        var handle = GetClipboardData(CF_HDROP);
        if (handle == IntPtr.Zero) return paths;
        var count = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFileW(handle, i, null, 0);
            var sb = new StringBuilder((int)length + 1);
            DragQueryFileW(handle, i, sb, length + 1);
            paths.Add(sb.ToString());
        }
        return paths;
    }

    /// <summary>Replace the clipboard contents with the given (format, bytes) pairs.</summary>
    public static bool Write(IntPtr owner, IReadOnlyList<(uint Format, byte[] Data)> formats)
    {
        var handles = new List<(uint Format, IntPtr Handle)>();
        void FreeAll()
        {
            foreach (var (_, h) in handles) GlobalFree(h);
        }

        foreach (var (format, data) in formats)
        {
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (h == IntPtr.Zero)
            {
                FreeAll();
                return false;
            }
            var ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(h);
                FreeAll();
                return false;
            }
            Marshal.Copy(data, 0, ptr, data.Length);
            GlobalUnlock(h);
            handles.Add((format, h));
        }

        if (!Open(owner))
        {
            FreeAll();
            return false;
        }
        try
        {
            EmptyClipboard();
            bool ok = true;
            foreach (var (format, h) in handles)
            {
                // On success the system owns the memory; on failure we must free it.
                if (SetClipboardData(format, h) == IntPtr.Zero)
                {
                    GlobalFree(h);
                    ok = false;
                }
            }
            return ok;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
