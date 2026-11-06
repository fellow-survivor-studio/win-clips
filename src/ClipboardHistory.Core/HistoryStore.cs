using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Core;

public enum EntryKind { Text, Files, Image }

/// <param name="Blob">For images: the name of the compressed image file in the blob directory.</param>
public sealed record HistoryEntry(long Id, EntryKind Kind, string Text, bool Pinned, string? Blob = null);

/// <summary>
/// SQLite-backed clipboard history. The schema, hashing and blob format match the
/// earlier Python version, so both read the same %LOCALAPPDATA%\ClipboardHistory data.
///
/// Each entry has a kind:
///   text  - text in <c>text</c>
///   files - newline-separated file paths in <c>text</c>
///   image - <c>text</c> is a label such as "[Image 1920x1080]"; the DIB bytes are stored
///           zlib-compressed in the blob directory as &lt;hash&gt;.z
/// Entries are de-duplicated by a SHA-256 hash of kind + content.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int MaxTextLength = 1_000_000;
    public const int MaxImageBytes = 30 * 1024 * 1024; // uncompressed DIB size limit

    private readonly SqliteConnection _conn;
    private readonly string _blobDir;

    public int MaxItems { get; set; }

    public HistoryStore(string dbPath, string blobDir, int maxItems = 200)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Directory.CreateDirectory(blobDir);
        _blobDir = blobDir;
        MaxItems = maxItems;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false, // release the file handle on Dispose
        }.ToString());
        _conn.Open();
        Migrate();
    }

    // ------------------------------------------------------------ helpers

    public static string KindName(EntryKind kind) => kind switch
    {
        EntryKind.Text => "text",
        EntryKind.Files => "files",
        EntryKind.Image => "image",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static EntryKind ParseKind(string name) => name switch
    {
        "files" => EntryKind.Files,
        "image" => EntryKind.Image,
        _ => EntryKind.Text,
    };

    public static string ContentHash(EntryKind kind, byte[] data)
    {
        var prefix = Encoding.ASCII.GetBytes(KindName(kind) + "\0");
        var all = new byte[prefix.Length + data.Length];
        prefix.CopyTo(all, 0);
        data.CopyTo(all, prefix.Length);
        return Convert.ToHexString(SHA256.HashData(all)).ToLowerInvariant();
    }

    /// <summary>Replace unpaired surrogates with '?' (same result as the Python version).</summary>
    public static string SanitizeText(string text)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool bad;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb?.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }
                bad = true;
            }
            else
            {
                bad = char.IsLowSurrogate(c);
            }
            if (bad)
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
                sb.Append('?');
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? text;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ schema

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS entries (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                kind      TEXT NOT NULL,
                hash      TEXT NOT NULL UNIQUE,
                text      TEXT NOT NULL,
                blob      TEXT,
                pinned    INTEGER NOT NULL DEFAULT 0,
                last_used REAL NOT NULL
            )
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_entries_order ON entries(pinned DESC, last_used DESC)");

        // Import the text-only table created by the first Python version, if present.
        using var check = Cmd("SELECT name FROM sqlite_master WHERE type='table' AND name='items'");
        if (check.ExecuteScalar() is null) return;

        using var tx = _conn.BeginTransaction();
        var legacy = new List<(string Text, long Pinned, double LastUsed)>();
        using (var read = Cmd("SELECT text, pinned, last_used FROM items"))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            while (r.Read())
                legacy.Add((r.GetString(0), r.GetInt64(1), r.GetDouble(2)));
        }
        foreach (var (text, pinned, lastUsed) in legacy)
        {
            var clean = SanitizeText(text);
            using var ins = Cmd(
                "INSERT OR IGNORE INTO entries (kind, hash, text, pinned, last_used) VALUES ('text', $h, $t, $p, $u)",
                ("$h", ContentHash(EntryKind.Text, Encoding.UTF8.GetBytes(clean))),
                ("$t", text), ("$p", pinned), ("$u", lastUsed));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
        using (var drop = Cmd("DROP TABLE items"))
        {
            drop.Transaction = tx;
            drop.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ------------------------------------------------------------ add

    public long? AddText(string? text)
    {
        if (text is null) return null;
        text = SanitizeText(text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return null;
        var h = ContentHash(EntryKind.Text, Encoding.UTF8.GetBytes(text));
        return Upsert(EntryKind.Text, h, text, null);
    }

    public long? AddFiles(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).Select(SanitizeText).ToList();
        if (list.Count == 0) return null;
        var text = string.Join("\n", list);
        var h = ContentHash(EntryKind.Files, Encoding.UTF8.GetBytes(text));
        return Upsert(EntryKind.Files, h, text, null);
    }

    public long? AddImage(byte[]? dib, string label)
    {
        if (dib is null || dib.Length == 0 || dib.Length > MaxImageBytes) return null;
        var h = ContentHash(EntryKind.Image, dib);
        var blob = h + ".z";
        var path = Path.Combine(_blobDir, blob);
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var z = new ZLibStream(fs, CompressionLevel.Optimal))
                z.Write(dib);
            File.Move(tmp, path, overwrite: true);
        }
        return Upsert(EntryKind.Image, h, label, blob);
    }

    private long Upsert(EntryKind kind, string hash, string text, string? blob)
    {
        var now = Now();
        long id;
        using (var find = Cmd("SELECT id FROM entries WHERE hash = $h", ("$h", hash)))
        {
            var existing = find.ExecuteScalar();
            if (existing is long found)
            {
                id = found;
                Exec("UPDATE entries SET last_used = $u WHERE id = $id", ("$u", now), ("$id", id));
            }
            else
            {
                using var ins = Cmd(
                    "INSERT INTO entries (kind, hash, text, blob, pinned, last_used) VALUES ($k, $h, $t, $b, 0, $u); SELECT last_insert_rowid();",
                    ("$k", KindName(kind)), ("$h", hash), ("$t", text), ("$b", blob), ("$u", now));
                id = (long)ins.ExecuteScalar()!;
            }
        }
        Prune();
        return id;
    }

    // ------------------------------------------------------------ maintenance

    private void Prune()
    {
        // Keep all pinned entries plus the newest MaxItems unpinned ones.
        DropRows(SelectRows("""
            SELECT id, blob FROM entries
            WHERE pinned = 0 AND id NOT IN (
                SELECT id FROM entries WHERE pinned = 0
                ORDER BY last_used DESC LIMIT $n
            )
            """, ("$n", (long)MaxItems)));
    }

    private List<(long Id, string? Blob)> SelectRows(string sql, params (string, object?)[] args)
    {
        var rows = new List<(long, string?)>();
        using var cmd = Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return rows;
    }

    private void DropRows(List<(long Id, string? Blob)> rows)
    {
        foreach (var (id, blob) in rows)
        {
            Exec("DELETE FROM entries WHERE id = $id", ("$id", id));
            if (string.IsNullOrEmpty(blob)) continue;
            // Another row can't share the blob (hash is unique), so it is safe to remove.
            foreach (var path in new[] { Path.Combine(_blobDir, blob), ThumbnailPath(blob) })
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // ------------------------------------------------------------ queries

    public List<HistoryEntry> Search(string? query = null, int limit = 500)
    {
        const string order = " ORDER BY pinned DESC, last_used DESC LIMIT $limit";
        SqliteCommand cmd;
        if (string.IsNullOrEmpty(query))
        {
            cmd = Cmd("SELECT id, kind, text, pinned, blob FROM entries" + order, ("$limit", (long)limit));
        }
        else
        {
            var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd = Cmd("SELECT id, kind, text, pinned, blob FROM entries WHERE text LIKE $q ESCAPE '\\'" + order,
                ("$q", "%" + escaped + "%"), ("$limit", (long)limit));
        }
        using (cmd)
        {
            var list = new List<HistoryEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HistoryEntry(r.GetInt64(0), ParseKind(r.GetString(1)), r.GetString(2), r.GetInt64(3) != 0,
                    r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }
    }

    public byte[]? GetImageDib(long id)
    {
        string? blob;
        using (var cmd = Cmd("SELECT kind, blob FROM entries WHERE id = $id", ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read() || r.GetString(0) != "image" || r.IsDBNull(1)) return null;
            blob = r.GetString(1);
        }
        return ReadImageBlob(blob);
    }

    /// <summary>
    /// Read and decompress an image blob by name. Touches only the file, not the database,
    /// so it may be called from a background thread.
    /// </summary>
    public byte[]? ReadImageBlob(string blob)
    {
        try
        {
            using var fs = File.OpenRead(Path.Combine(_blobDir, Path.GetFileName(blob)));
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the list thumbnail (a small PNG made by the app) for an image blob is cached.
    /// It is deleted together with the entry.
    /// </summary>
    public string ThumbnailPath(string blob) =>
        Path.Combine(_blobDir, Path.GetFileNameWithoutExtension(Path.GetFileName(blob)) + ".thumb.png");

    // ------------------------------------------------------------ user actions

    public void Touch(long id) =>
        Exec("UPDATE entries SET last_used = $u WHERE id = $id", ("$u", Now()), ("$id", id));

    public bool TogglePin(long id)
    {
        Exec("UPDATE entries SET pinned = 1 - pinned WHERE id = $id", ("$id", id));
        using var cmd = Cmd("SELECT pinned FROM entries WHERE id = $id", ("$id", id));
        return cmd.ExecuteScalar() is long p && p != 0;
    }

    public void Delete(long id) => DropRows(SelectRows("SELECT id, blob FROM entries WHERE id = $id", ("$id", id)));

    public void Clear(bool keepPinned = true) =>
        DropRows(SelectRows("SELECT id, blob FROM entries" + (keepPinned ? " WHERE pinned = 0" : "")));

    public void Dispose() => _conn.Dispose();
}
