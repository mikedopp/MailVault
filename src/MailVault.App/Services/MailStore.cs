using System.Text;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailVault.Services;

public record MessageRow(
    long Id, string Path, long DateUtc, string Subject, string Sender,
    string Recipients, long Size, int AttachCount, string AttachNames, bool Deleted);

public record AttachmentInfo(int Index, string Name, string ContentType, long Size);

public record SearchResult(List<MessageRow> Rows, int Total);

/// <summary>
/// Indexes a folder of .eml files (a GYB backup) into a SQLite FTS5 database
/// stored inside that folder, and provides search + trash-based delete.
/// The GYB msg-db.sqlite is never touched; deletes move .eml files into
/// _MailVaultTrash preserving relative paths so restore is exact.
/// </summary>
public sealed class MailStore : IDisposable
{
    public const string IndexFileName = "MailVault.index.db";
    public const string TrashDirName = "_MailVaultTrash";

    private SqliteConnection? _db;
    public string? Root { get; private set; }

    public void Open(string folder)
    {
        Close();
        Root = System.IO.Path.GetFullPath(folder);
        var dbPath = System.IO.Path.Combine(Root, IndexFileName);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        // schema v3: content-storing fts (contentless can't DELETE) + messageId
        // column so purges can be synced back to the live Gmail account
        long ver;
        using (var v = _db.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            ver = (long)v.ExecuteScalar()!;
        }
        if (ver != 3)
        {
            Exec("DROP TABLE IF EXISTS fts; DROP TABLE IF EXISTS messages; PRAGMA user_version=3;");
        }
        Exec("""
            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY,
                path TEXT UNIQUE NOT NULL,
                mtime INTEGER NOT NULL,
                dateUtc INTEGER NOT NULL,
                subject TEXT NOT NULL DEFAULT '',
                sender TEXT NOT NULL DEFAULT '',
                recipients TEXT NOT NULL DEFAULT '',
                size INTEGER NOT NULL DEFAULT 0,
                attachCount INTEGER NOT NULL DEFAULT 0,
                attachNames TEXT NOT NULL DEFAULT '',
                messageId TEXT NOT NULL DEFAULT '',
                deleted INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_messages_date ON messages(dateUtc);
            CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(
                subject, sender, recipients, body, attachNames,
                tokenize='unicode61');
            """);
    }

    public void Close() { _db?.Dispose(); _db = null; Root = null; }
    public void Dispose() => Close();

    private SqliteConnection Db => _db ?? throw new InvalidOperationException("No folder open");

    private void Exec(string sql)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------- indexing ----------

    public (int indexed, int removed) IndexFolder(Action<int, int>? progress, CancellationToken ct)
    {
        var root = Root!;
        var trashPrefix = System.IO.Path.Combine(root, TrashDirName);
        var onDisk = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); // relpath -> mtime
        foreach (var f in Directory.EnumerateFiles(root, "*.eml", SearchOption.AllDirectories))
        {
            if (f.StartsWith(trashPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            onDisk[System.IO.Path.GetRelativePath(root, f)] =
                new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds();
        }

        // current index state (non-deleted rows must exist on disk)
        var known = new Dictionary<string, (long id, long mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, mtime FROM messages WHERE deleted=0";
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2));
        }

        int removed = 0;
        foreach (var (path, (id, _)) in known)
        {
            if (!onDisk.ContainsKey(path))
            {
                using var del = Db.CreateCommand();
                del.CommandText = "DELETE FROM messages WHERE id=$id; DELETE FROM fts WHERE rowid=$id;";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
                removed++;
            }
        }

        var toIndex = onDisk.Where(kv => !known.TryGetValue(kv.Key, out var k) || k.mtime != kv.Value)
                            .Select(kv => kv.Key).ToList();
        int done = 0, total = toIndex.Count;
        progress?.Invoke(0, total);

        using var tx = Db.BeginTransaction();
        foreach (var rel in toIndex)
        {
            ct.ThrowIfCancellationRequested();
            try { IndexOne(rel, onDisk[rel]); } catch { /* unparseable file: skip */ }
            done++;
            if (done % 200 == 0)
            {
                tx.Commit(); // checkpoint so progress survives interruption
                Db.BeginTransaction();
                progress?.Invoke(done, total);
            }
        }
        tx.Commit();
        progress?.Invoke(done, total);
        return (done, removed);
    }

    private void IndexOne(string relPath, long mtime)
    {
        var full = System.IO.Path.Combine(Root!, relPath);
        var msg = MimeMessage.Load(full);
        var subject = msg.Subject ?? "";
        var sender = msg.From?.ToString() ?? "";
        var recipients = string.Join(", ",
            new[] { msg.To?.ToString(), msg.Cc?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
        var date = msg.Date == default ? 0 : msg.Date.ToUnixTimeSeconds();
        var size = new FileInfo(full).Length;

        var attachNames = new List<string>();
        foreach (var att in msg.Attachments)
            attachNames.Add(att is MimePart mp ? (mp.FileName ?? "unnamed") : "message.eml");

        var body = msg.TextBody ?? StripHtml(msg.HtmlBody) ?? "";
        if (body.Length > 200_000) body = body[..200_000];

        // replace any stale row for this path
        long oldId = -1;
        using (var q = Db.CreateCommand())
        {
            q.CommandText = "SELECT id FROM messages WHERE path=$p";
            q.Parameters.AddWithValue("$p", relPath);
            oldId = (q.ExecuteScalar() as long?) ?? -1;
        }
        if (oldId >= 0)
        {
            using var d = Db.CreateCommand();
            d.CommandText = "DELETE FROM messages WHERE id=$id; DELETE FROM fts WHERE rowid=$id;";
            d.Parameters.AddWithValue("$id", oldId);
            d.ExecuteNonQuery();
        }

        using var ins = Db.CreateCommand();
        ins.CommandText = """
            INSERT INTO messages(path, mtime, dateUtc, subject, sender, recipients, size, attachCount, attachNames, messageId)
            VALUES($p,$m,$d,$s,$f,$r,$z,$ac,$an,$mid);
            """;
        ins.Parameters.AddWithValue("$mid", msg.MessageId ?? "");
        ins.Parameters.AddWithValue("$p", relPath);
        ins.Parameters.AddWithValue("$m", mtime);
        ins.Parameters.AddWithValue("$d", date);
        ins.Parameters.AddWithValue("$s", subject);
        ins.Parameters.AddWithValue("$f", sender);
        ins.Parameters.AddWithValue("$r", recipients);
        ins.Parameters.AddWithValue("$z", size);
        ins.Parameters.AddWithValue("$ac", attachNames.Count);
        ins.Parameters.AddWithValue("$an", string.Join("; ", attachNames));
        ins.ExecuteNonQuery();

        using var last = Db.CreateCommand();
        last.CommandText = "SELECT last_insert_rowid()";
        var newId = (long)last.ExecuteScalar()!;

        using var fts = Db.CreateCommand();
        fts.CommandText = """
            INSERT INTO fts(rowid, subject, sender, recipients, body, attachNames)
            VALUES($id,$s,$f,$r,$b,$an);
            """;
        fts.Parameters.AddWithValue("$id", newId);
        fts.Parameters.AddWithValue("$s", subject);
        fts.Parameters.AddWithValue("$f", sender);
        fts.Parameters.AddWithValue("$r", recipients);
        fts.Parameters.AddWithValue("$b", body);
        fts.Parameters.AddWithValue("$an", string.Join("; ", attachNames));
        fts.ExecuteNonQuery();
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        foreach (var c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') { inTag = false; sb.Append(' '); }
            else if (!inTag) sb.Append(c);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    // ---------- search ----------

    public SearchResult Search(string? query, string? from, long? dateFrom, long? dateTo,
        bool? hasAttach, bool deletedView, string sort, int offset, int limit)
    {
        var where = new List<string> { "m.deleted=" + (deletedView ? "1" : "0") };
        var join = "";
        var p = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            join = "JOIN fts ON fts.rowid = m.id";
            where.Add("fts MATCH $q");
            p.Add(new SqliteParameter("$q", BuildFtsQuery(query)));
        }
        if (!string.IsNullOrWhiteSpace(from))
        {
            where.Add("m.sender LIKE $from");
            p.Add(new SqliteParameter("$from", "%" + from.Trim() + "%"));
        }
        if (dateFrom is not null) { where.Add("m.dateUtc >= $df"); p.Add(new SqliteParameter("$df", dateFrom)); }
        if (dateTo is not null) { where.Add("m.dateUtc <= $dt"); p.Add(new SqliteParameter("$dt", dateTo)); }
        if (hasAttach == true) where.Add("m.attachCount > 0");

        var order = sort switch
        {
            "dateAsc" => "m.dateUtc ASC",
            "sender" => "m.sender COLLATE NOCASE ASC, m.dateUtc DESC",
            "subject" => "m.subject COLLATE NOCASE ASC, m.dateUtc DESC",
            "sizeDesc" => "m.size DESC",
            _ => "m.dateUtc DESC",
        };

        var whereSql = string.Join(" AND ", where);
        int total;
        using (var cnt = Db.CreateCommand())
        {
            cnt.CommandText = $"SELECT COUNT(*) FROM messages m {join} WHERE {whereSql}";
            foreach (var x in p) cnt.Parameters.Add(new SqliteParameter(x.ParameterName, x.Value));
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        var rows = new List<MessageRow>();
        using (var cmd = Db.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT m.id, m.path, m.dateUtc, m.subject, m.sender, m.recipients,
                       m.size, m.attachCount, m.attachNames, m.deleted
                FROM messages m {join} WHERE {whereSql}
                ORDER BY {order} LIMIT $lim OFFSET $off
                """;
            foreach (var x in p) cmd.Parameters.Add(new SqliteParameter(x.ParameterName, x.Value));
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", offset);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new MessageRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt32(7), r.GetString(8),
                    r.GetInt64(9) != 0));
        }
        return new SearchResult(rows, total);
    }

    private static string BuildFtsQuery(string raw)
    {
        // each whitespace-separated term becomes a quoted prefix term; robust
        // against FTS5 operator characters in user input
        var terms = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));
    }

    // ---------- message detail ----------

    public string GetFullPath(long id)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = "SELECT path, deleted FROM messages WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new FileNotFoundException("message not in index");
        var rel = r.GetString(0);
        var deleted = r.GetInt64(1) != 0;
        return System.IO.Path.Combine(Root!, deleted ? System.IO.Path.Combine(TrashDirName, rel) : rel);
    }

    public (MimeMessage msg, List<AttachmentInfo> atts) LoadMessage(long id)
    {
        var msg = MimeMessage.Load(GetFullPath(id));
        var atts = new List<AttachmentInfo>();
        int i = 0;
        foreach (var att in msg.Attachments)
        {
            string name = att is MimePart mp ? (mp.FileName ?? $"attachment{i}") : $"attached-message{i}.eml";
            long size = 0;
            if (att is MimePart part && part.Content != null)
            {
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                size = ms.Length;
            }
            atts.Add(new AttachmentInfo(i, name, att.ContentType?.MimeType ?? "application/octet-stream", size));
            i++;
        }
        return (msg, atts);
    }

    public (string name, byte[] bytes) GetAttachment(long id, int index)
    {
        var (msg, _) = LoadMessage(id);
        var att = msg.Attachments.ElementAt(index);
        using var ms = new MemoryStream();
        if (att is MimePart mp)
        {
            mp.Content.DecodeTo(ms);
            return (mp.FileName ?? $"attachment{index}", ms.ToArray());
        }
        ((MessagePart)att).Message.WriteTo(ms);
        return ($"attached-message{index}.eml", ms.ToArray());
    }

    // ---------- CRUD: trash / restore / purge ----------

    public int MoveToTrash(IEnumerable<long> ids)
    {
        int n = 0;
        foreach (var id in ids)
        {
            string rel;
            using (var q = Db.CreateCommand())
            {
                q.CommandText = "SELECT path FROM messages WHERE id=$id AND deleted=0";
                q.Parameters.AddWithValue("$id", id);
                rel = q.ExecuteScalar() as string ?? "";
            }
            if (rel == "") continue;
            var src = System.IO.Path.Combine(Root!, rel);
            var dst = System.IO.Path.Combine(Root!, TrashDirName, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            using var u = Db.CreateCommand();
            u.CommandText = "UPDATE messages SET deleted=1 WHERE id=$id";
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
            n++;
        }
        return n;
    }

    public int RestoreFromTrash(IEnumerable<long> ids)
    {
        int n = 0;
        foreach (var id in ids)
        {
            string rel;
            using (var q = Db.CreateCommand())
            {
                q.CommandText = "SELECT path FROM messages WHERE id=$id AND deleted=1";
                q.Parameters.AddWithValue("$id", id);
                rel = q.ExecuteScalar() as string ?? "";
            }
            if (rel == "") continue;
            var src = System.IO.Path.Combine(Root!, TrashDirName, rel);
            var dst = System.IO.Path.Combine(Root!, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            using var u = Db.CreateCommand();
            u.CommandText = "UPDATE messages SET deleted=0 WHERE id=$id";
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
            n++;
        }
        return n;
    }

    public const string PurgeManifestName = "_PurgedFromArchive.jsonl";

    public int PurgeTrash()
    {
        int n;
        using (var cnt = Db.CreateCommand())
        {
            cnt.CommandText = "SELECT COUNT(*) FROM messages WHERE deleted=1";
            n = Convert.ToInt32(cnt.ExecuteScalar());
        }

        // record what is being purged so deletions can optionally be synced
        // back to the live Gmail account later (GoogleExit\Invoke-GmailSyncDeletes.ps1)
        var manifest = new StringBuilder();
        using (var q = Db.CreateCommand())
        {
            q.CommandText = "SELECT messageId, subject, sender, dateUtc FROM messages WHERE deleted=1";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                manifest.AppendLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    messageId = r.GetString(0),
                    subject = r.GetString(1),
                    sender = r.GetString(2),
                    dateUtc = r.GetInt64(3),
                    purgedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }));
            }
        }
        if (manifest.Length > 0)
            File.AppendAllText(System.IO.Path.Combine(Root!, PurgeManifestName), manifest.ToString());

        var trash = System.IO.Path.Combine(Root!, TrashDirName);
        if (Directory.Exists(trash)) Directory.Delete(trash, recursive: true);
        Exec("DELETE FROM fts WHERE rowid IN (SELECT id FROM messages WHERE deleted=1); DELETE FROM messages WHERE deleted=1;");
        return n;
    }

    public object Stats()
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM messages WHERE deleted=0),
              (SELECT IFNULL(SUM(size),0) FROM messages WHERE deleted=0),
              (SELECT COUNT(*) FROM messages WHERE deleted=1),
              (SELECT IFNULL(SUM(size),0) FROM messages WHERE deleted=1),
              (SELECT COUNT(*) FROM messages WHERE deleted=0 AND attachCount>0)
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new
        {
            count = r.GetInt64(0), bytes = r.GetInt64(1),
            trashCount = r.GetInt64(2), trashBytes = r.GetInt64(3),
            withAttachments = r.GetInt64(4), root = Root,
        };
    }
}
