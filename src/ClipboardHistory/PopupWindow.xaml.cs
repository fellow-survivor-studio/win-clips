using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardHistory.Core;
using ClipboardHistory.Interop;
using ClipboardHistory.Services;

namespace ClipboardHistory;

/// <summary>One row of the history list.</summary>
public sealed class EntryItem
{
    public EntryItem(HistoryEntry entry, int index)
    {
        Entry = entry;
        Preview = ClipboardData.ListPreview(entry);
        Glyph = entry.Kind switch
        {
            EntryKind.Files => "",  // folder
            EntryKind.Image => "",  // photo
            _ => "",                // document
        };
        PinVisibility = entry.Pinned ? Visibility.Visible : Visibility.Collapsed;
        Shortcut = index < 9 ? $"Ctrl+{index + 1}" : "";
    }

    public HistoryEntry Entry { get; }
    public string Preview { get; }
    public string Glyph { get; }
    public Visibility PinVisibility { get; }
    public string Shortcut { get; }
}

public partial class PopupWindow : Window
{
    private const int PreviewTextChars = 3000;
    private const int ImageCacheSize = 16;

    private readonly App _app;
    private readonly Dictionary<long, BitmapSource> _imageCache = new();
    private readonly LinkedList<long> _imageOrder = new();
    private List<EntryItem> _items = new();
    private IntPtr _target;
    private bool _modalOpen;
    private bool _exiting;
    private Point? _lastMouse;
    private EntryItem? _pressedItem;

    internal PopupWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Theme.Apply(Resources);
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) =>
        {
            // Clicking anywhere else closes the popup, like a menu.
            if (!_modalOpen && IsVisible) HidePopup();
        };
        UpdateFooter();
    }

    // ------------------------------------------------------------ show / hide

    internal void ShowPopup(IntPtr target)
    {
        _target = target;
        Theme.Apply(Resources);
        UpdateFooter();
        if (SearchBox.Text.Length > 0) SearchBox.Text = ""; // triggers Refresh
        Refresh();
        _lastMouse = null;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Input.PlaceNearCursor(hwnd, Width, Height);
        Show();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    internal void HidePopup()
    {
        if (IsVisible) Hide();
    }

    internal void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true; // Alt+F4 only hides; the app keeps running in the tray
            HidePopup();
        }
        base.OnClosing(e);
    }

    internal void UpdateFooter()
    {
        // Opened from the tray there is no window to paste into, so Enter only copies.
        var enter = _target == IntPtr.Zero ? "Enter 복사" : "Enter 붙여넣기";
        Footer.Text = $"{enter} · Ctrl+P 고정 · Ctrl+Del 삭제 · Ctrl+L 비우기 · Esc 닫기";
    }

    /// <summary>Called by the app when entries were added or removed.</summary>
    internal void OnHistoryChanged()
    {
        if (!IsVisible) return;
        Refresh(Selected?.Entry.Id);
    }

    // ------------------------------------------------------------ list

    private EntryItem? Selected => List.SelectedItem as EntryItem;

    private void Refresh(long? selectId = null)
    {
        List<HistoryEntry> entries;
        try
        {
            entries = _app.Store.Search(SearchBox.Text);
        }
        catch (Exception ex)
        {
            Log.Error("search failed", ex);
            entries = new();
        }
        _items = entries.Select((e, i) => new EntryItem(e, i)).ToList();
        List.ItemsSource = _items;
        CountText.Text = $"{_items.Count}개";
        bool empty = _items.Count == 0;
        EmptyText.Text = SearchBox.Text.Length > 0 ? "일치하는 항목이 없습니다" : "기록이 없습니다";
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            ShowPreview(null);
            return;
        }
        int index = 0;
        if (selectId is { } id)
            index = Math.Max(0, _items.FindIndex(it => it.Entry.Id == id));
        Select(index);
    }

    private void Select(int index)
    {
        if (_items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        List.SelectedIndex = index;
        List.ScrollIntoView(_items[index]);
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        Select(List.SelectedIndex < 0 ? 0 : List.SelectedIndex + delta);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPreview(Selected);

    // ------------------------------------------------------------ preview

    private void ShowPreview(EntryItem? item)
    {
        PreviewImage.Source = null;
        PreviewText.Text = "";
        if (item is null) return;
        var entry = item.Entry;
        switch (entry.Kind)
        {
            case EntryKind.Image:
                var image = LoadImage(entry.Id);
                if (image is not null)
                    PreviewImage.Source = image;
                else
                    PreviewText.Text = entry.Text + " (미리보기를 표시할 수 없습니다)";
                break;
            case EntryKind.Files:
                PreviewText.Text = entry.Text.Replace("\n", Environment.NewLine);
                break;
            default:
                PreviewText.Text = entry.Text.Length > PreviewTextChars ? entry.Text[..PreviewTextChars] + "…" : entry.Text;
                break;
        }
    }

    private BitmapSource? LoadImage(long id)
    {
        if (_imageCache.TryGetValue(id, out var cached)) return cached;
        try
        {
            var dib = _app.Store.GetImageDib(id);
            if (dib is null) return null;
            var bmp = ClipboardData.DibToBmp(dib);
            var size = ClipboardData.DibSize(dib);
            if (bmp is null || size is null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bmp);
            // The preview box is small; decoding at full size would waste memory on big screenshots.
            if (size.Value.Width > 900) image.DecodePixelWidth = 900;
            image.EndInit();
            image.Freeze();

            _imageCache[id] = image;
            _imageOrder.AddLast(id);
            while (_imageOrder.Count > ImageCacheSize)
            {
                _imageCache.Remove(_imageOrder.First!.Value);
                _imageOrder.RemoveFirst();
            }
            return image;
        }
        catch (Exception ex)
        {
            Log.Error($"could not decode image {id}", ex);
            return null;
        }
    }

    private void ForgetImage(long id)
    {
        if (_imageCache.Remove(id)) _imageOrder.Remove(id);
    }

    // ------------------------------------------------------------ actions

    private async void PasteItem(EntryItem? item)
    {
        if (item is null) return;
        var target = _target;
        _target = IntPtr.Zero;
        HidePopup();
        try
        {
            await _app.PasteAsync(item.Entry, target);
        }
        catch (Exception ex)
        {
            Log.Error("paste failed", ex);
        }
    }

    private void TogglePin()
    {
        if (Selected is not { } item) return;
        _app.Store.TogglePin(item.Entry.Id);
        Refresh(item.Entry.Id);
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item) return;
        int index = List.SelectedIndex;
        ForgetImage(item.Entry.Id);
        _app.Store.Delete(item.Entry.Id);
        Refresh();
        Select(index);
    }

    private void ClearUnpinned()
    {
        _modalOpen = true;
        try
        {
            if (_app.ConfirmClearUnpinned(this))
            {
                _imageCache.Clear();
                _imageOrder.Clear();
            }
        }
        finally
        {
            _modalOpen = false;
        }
        Activate();
        SearchBox.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        e.Handled = true;
        switch (key)
        {
            case Key.Down: Move(1); return;
            case Key.Up: Move(-1); return;
            case Key.PageDown: Move(8); return;
            case Key.PageUp: Move(-8); return;
            case Key.Enter: PasteItem(Selected); return;
            case Key.Escape: HidePopup(); return;
            case Key.P when ctrl: TogglePin(); return;
            case Key.Delete when ctrl: DeleteSelected(); return;
            case Key.L when ctrl: ClearUnpinned(); return;
            case >= Key.D1 and <= Key.D9 when ctrl:
                PasteNth(key - Key.D1);
                return;
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl:
                PasteNth(key - Key.NumPad1);
                return;
        }
        e.Handled = false;
    }

    private void PasteNth(int index)
    {
        if (index < _items.Count) PasteItem(_items[index]);
    }

    // ------------------------------------------------------------ mouse: hover selects, click pastes

    private EntryItem? ItemAt(MouseEventArgs e)
    {
        if (List.InputHitTest(e.GetPosition(List)) is not DependencyObject hit) return null;
        return ItemsControl.ContainerFromElement(List, hit) is ListBoxItem container
            ? container.DataContext as EntryItem
            : null;
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        // WPF also raises MouseMove when the list scrolls under a still mouse;
        // only follow real movement so keyboard navigation is not overridden.
        var pos = e.GetPosition(this);
        if (_lastMouse is { } last && (pos - last).Length < 1) return;
        bool first = _lastMouse is null;
        _lastMouse = pos;
        if (first) return; // the popup just opened under the cursor
        if (ItemAt(e) is { } item && !ReferenceEquals(item, Selected))
            List.SelectedItem = item;
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = ItemAt(e);
        if (_pressedItem is not null)
        {
            List.SelectedItem = _pressedItem;
            e.Handled = true; // keep keyboard focus in the search box
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e);
        if (item is not null && ReferenceEquals(item, _pressedItem))
        {
            e.Handled = true;
            PasteItem(item);
        }
        _pressedItem = null;
    }
}
