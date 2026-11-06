using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipboardHistory.Core;

namespace ClipboardHistory;

/// <summary>One row of the history list.</summary>
public sealed partial class EntryItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;

    public EntryItem(HistoryEntry entry, int index)
    {
        Entry = entry;
        bool image = entry.Kind == EntryKind.Image;
        Preview = ClipboardData.ListPreview(entry);
        Glyph = entry.Kind switch
        {
            EntryKind.Files => "",  // folder
            EntryKind.Image => "",  // photo
            _ => "",                // document
        };
        PinVisibility = entry.Pinned ? Visibility.Visible : Visibility.Collapsed;
        Shortcut = index < 9 ? $"Ctrl+{index + 1}" : "";
        TextVisibility = image ? Visibility.Collapsed : Visibility.Visible;
        ImageVisibility = image ? Visibility.Visible : Visibility.Collapsed;
        ImageSize = image && SizeLabel().Match(entry.Text) is { Success: true } m
            ? $"{m.Groups[1].Value} × {m.Groups[2].Value}"
            : "";
    }

    public HistoryEntry Entry { get; }
    public string Preview { get; }
    public string Glyph { get; }
    public Visibility PinVisibility { get; }
    public string Shortcut { get; }

    /// <summary>Text rows show the preview text; image rows show a thumbnail instead.</summary>
    public Visibility TextVisibility { get; }
    public Visibility ImageVisibility { get; }

    /// <summary>"1920 × 1080", shown next to the thumbnail.</summary>
    public string ImageSize { get; }

    /// <summary>Filled in the background after the row is shown.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    [GeneratedRegex(@"(\d+)x(\d+)")]
    private static partial Regex SizeLabel();
}
