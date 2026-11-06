using System.Text;
using ClipboardHistory.Core;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cbh-tests-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "history.db");
    private string BlobDir => Path.Combine(_dir, "images");

    private HistoryStore Open(int maxItems = 200) => new(DbPath, BlobDir, maxItems);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] FakeDib(int w, int h, byte fill)
    {
        var stride = (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        for (int i = 40; i < dib.Length; i++) dib[i] = fill;
        return dib;
    }

    [Fact]
    public void Text_is_deduplicated_and_moved_to_top()
    {
        using var s = Open();
        var a = s.AddText("alpha");
        s.AddText("beta");
        Thread.Sleep(5);
        var again = s.AddText("alpha");
        Assert.Equal(a, again);
        var items = s.Search();
        Assert.Equal(new[] { "alpha", "beta" }, items.Select(i => i.Text));
    }

    [Fact]
    public void Blank_and_oversized_text_is_ignored()
    {
        using var s = Open();
        Assert.Null(s.AddText("   \n\t"));
        Assert.Null(s.AddText(null));
        Assert.Null(s.AddText(new string('x', HistoryStore.MaxTextLength + 1)));
        Assert.Empty(s.Search());
    }

    [Fact]
    public void Lone_surrogates_are_replaced()
    {
        using var s = Open();
        s.AddText("a\ud800b");
        Assert.Equal("a?b", s.Search().Single().Text);
        Assert.Equal("ok\ud83d\ude00", HistoryStore.SanitizeText("ok\ud83d\ude00")); // valid pair kept
    }

    [Fact]
    public void Prune_keeps_newest_and_pinned()
    {
        using var s = Open(maxItems: 3);
        var first = s.AddText("t0")!.Value;
        s.TogglePin(first);
        for (int i = 1; i <= 5; i++)
        {
            Thread.Sleep(2);
            s.AddText("t" + i);
        }
        var texts = s.Search().Select(e => e.Text).ToList();
        Assert.Equal(new[] { "t0", "t5", "t4", "t3" }, texts);
        Assert.True(s.Search().First().Pinned);
    }

    [Fact]
    public void Search_escapes_like_wildcards()
    {
        using var s = Open();
        s.AddText("100% done");
        s.AddText("100 done");
        s.AddText("a_b");
        s.AddText("axb");
        Assert.Equal("100% done", s.Search("0%").Single().Text);
        Assert.Equal("a_b", s.Search("a_").Single().Text);
        Assert.Equal(2, s.Search("DONE").Count); // case-insensitive for ASCII
    }

    [Fact]
    public void Files_entry_round_trips()
    {
        using var s = Open();
        s.AddFiles(new[] { @"C:\a\one.txt", "", @"D:\two" });
        var e = s.Search().Single();
        Assert.Equal(EntryKind.Files, e.Kind);
        Assert.Equal("C:\\a\\one.txt\nD:\\two", e.Text);
        Assert.Null(s.AddFiles(Array.Empty<string>()));
    }

    [Fact]
    public void Image_round_trips_and_blob_is_removed_on_delete()
    {
        using var s = Open();
        var dib = FakeDib(4, 3, 0x7F);
        var id = s.AddImage(dib, "[Image 4x3]")!.Value;
        Assert.Equal(dib, s.GetImageDib(id));
        Assert.Single(Directory.GetFiles(BlobDir));
        s.Delete(id);
        Assert.Empty(Directory.GetFiles(BlobDir));
        Assert.Null(s.GetImageDib(id));
    }

    [Fact]
    public void Image_thumbnail_is_listed_and_removed_with_the_entry()
    {
        using var s = Open();
        var dib = FakeDib(4, 3, 0x10);
        var id = s.AddImage(dib, "[Image 4x3]")!.Value;
        var entry = s.Search().Single();
        Assert.NotNull(entry.Blob);
        Assert.Equal(dib, s.ReadImageBlob(entry.Blob!));

        var thumb = s.ThumbnailPath(entry.Blob!);
        Assert.Equal(BlobDir, Path.GetDirectoryName(thumb));
        Assert.EndsWith(".thumb.png", thumb);
        File.WriteAllBytes(thumb, new byte[] { 1, 2, 3 }); // the app writes the real PNG
        s.Delete(id);
        Assert.False(File.Exists(thumb));
        Assert.Empty(Directory.GetFiles(BlobDir));
    }

    [Fact]
    public void Text_entries_have_no_blob()
    {
        using var s = Open();
        s.AddText("plain");
        Assert.Null(s.Search().Single().Blob);
    }

    [Fact]
    public void Pruned_images_lose_their_blob()
    {
        using var s = Open(maxItems: 1);
        s.AddImage(FakeDib(2, 2, 1), "[Image 2x2]");
        Thread.Sleep(2);
        s.AddImage(FakeDib(2, 2, 2), "[Image 2x2]");
        Assert.Single(s.Search());
        Assert.Single(Directory.GetFiles(BlobDir));
    }

    [Fact]
    public void Clear_keeps_pinned()
    {
        using var s = Open();
        var keep = s.AddText("keep")!.Value;
        s.TogglePin(keep);
        s.AddText("drop");
        s.Clear(keepPinned: true);
        Assert.Equal("keep", s.Search().Single().Text);
        s.Clear(keepPinned: false);
        Assert.Empty(s.Search());
    }

    [Fact]
    public void Hash_matches_python_version()
    {
        // Python: hashlib.sha256(b"text\0hello").hexdigest()
        Assert.Equal("306f89347195cc05509ddca47462e259aac6fd01943d2557004ea6f26370cd58",
            HistoryStore.ContentHash(EntryKind.Text, Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void Re_adding_text_stored_by_python_does_not_duplicate()
    {
        using (var s = Open()) s.AddText("seed"); // creates the schema
        using (var c = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO entries (kind, hash, text, pinned, last_used) VALUES " +
                              "('text', '306f89347195cc05509ddca47462e259aac6fd01943d2557004ea6f26370cd58', 'hello', 0, 1.0)";
            cmd.ExecuteNonQuery();
        }
        using var store = Open();
        store.AddText("hello");
        Assert.Equal(2, store.Search().Count);
        Assert.Equal("hello", store.Search().First().Text);
    }

    [Fact]
    public void Legacy_items_table_is_migrated()
    {
        Directory.CreateDirectory(_dir);
        using (var c = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE items (id INTEGER PRIMARY KEY, text TEXT, pinned INTEGER, last_used REAL);
                INSERT INTO items (text, pinned, last_used) VALUES ('old one', 1, 10.0), ('old two', 0, 20.0);
                """;
            cmd.ExecuteNonQuery();
        }
        using var s = Open();
        var items = s.Search();
        Assert.Equal(new[] { "old one", "old two" }, items.Select(i => i.Text));
        Assert.True(items[0].Pinned);
    }
}
