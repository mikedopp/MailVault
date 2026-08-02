using MailVault.Services;

namespace MailVault;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--scan")
            return Scan(args[1]);

        if (args.Length >= 2 && args[0] == "--smoke")
            return Smoke(args[1], args.Contains("--force"));

        // MailVault.exe <folder>  opens that archive immediately
        var startFolder = args.Length == 1 && Directory.Exists(args[0]) ? args[0] : null;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startFolder));
        return 0;
    }

    /// <summary>Read-only: index a real archive and report what's in it. Never modifies mail.</summary>
    private static int Scan(string folder)
    {
        try
        {
            using var store = new MailStore();
            store.Open(folder);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastPct = -1;
            var (indexed, removed) = store.IndexFolder((done, total) =>
            {
                if (total == 0) return;
                var pct = done * 100 / total;
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    Console.WriteLine($"  indexing {pct}%  ({done:N0}/{total:N0})");
                }
            }, CancellationToken.None);
            sw.Stop();
            Console.WriteLine($"indexed={indexed:N0} removed={removed} in {sw.Elapsed.TotalSeconds:N0}s");

            dynamic s = store.Stats();
            Console.WriteLine($"messages={s.count:N0}  size={(double)s.bytes / (1024 * 1024 * 1024):N2} GB  withAttachments={s.withAttachments:N0}");

            var all = store.Search(null, null, null, null, null, false, "dateDesc", 0, 5);
            Console.WriteLine($"\nNewest 5 of {all.Total:N0}:");
            foreach (var r in all.Rows)
                Console.WriteLine($"  {DateTimeOffset.FromUnixTimeSeconds(r.DateUtc).LocalDateTime:yyyy-MM-dd}  {Trim(r.Sender, 38)}  {Trim(r.Subject, 46)}");

            var oldest = store.Search(null, null, null, null, null, false, "dateAsc", 0, 3);
            Console.WriteLine("\nOldest 3:");
            foreach (var r in oldest.Rows)
                Console.WriteLine($"  {DateTimeOffset.FromUnixTimeSeconds(r.DateUtc).LocalDateTime:yyyy-MM-dd}  {Trim(r.Sender, 38)}  {Trim(r.Subject, 46)}");

            var big = store.Search(null, null, null, null, true, false, "sizeDesc", 0, 3);
            Console.WriteLine($"\nLargest 3 with attachments (of {big.Total:N0}):");
            foreach (var r in big.Rows)
                Console.WriteLine($"  {r.Size / (1024 * 1024)} MB  {Trim(r.Subject, 40)}  [{Trim(r.AttachNames, 40)}]");

            foreach (var term in new[] { "invoice", "password", "receipt", "lawyer" })
            {
                var hit = store.Search(term, null, null, null, null, false, "dateDesc", 0, 1);
                Console.WriteLine($"search '{term}' -> {hit.Total:N0} message(s)");
            }

            Console.WriteLine("\nSCAN OK (read-only - nothing was modified)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SCAN FAIL: " + ex.Message);
            return 1;
        }
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.PadRight(n) : s[..(n - 1)] + "…");

    /// <summary>
    /// Headless exercise of the full store pipeline. DESTRUCTIVE: trashes and purges a
    /// message, so it refuses to touch anything that looks like a real archive.
    /// </summary>
    private static int Smoke(string folder, bool force)
    {
        try
        {
            var emlCount = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.eml", SearchOption.AllDirectories).Take(101).Count()
                : 0;
            if (emlCount > 100 && !force)
            {
                Console.WriteLine($"REFUSED: {folder} holds {emlCount}+ .eml files and looks like a real archive.");
                Console.WriteLine("--smoke permanently deletes a message. Use --scan for real data,");
                Console.WriteLine("or pass --force if you really mean it.");
                return 2;
            }

            using var store = new MailStore();
            store.Open(folder);
            var (indexed, removed) = store.IndexFolder(null, CancellationToken.None);
            Console.WriteLine($"indexed={indexed} removed={removed}");

            var all = store.Search(null, null, null, null, null, false, "dateDesc", 0, 100);
            Console.WriteLine($"total={all.Total}");
            foreach (var r in all.Rows)
                Console.WriteLine($"  [{r.Id}] {r.Sender} | {r.Subject} | atts={r.AttachCount}");

            var q = store.Search("invoice", null, null, null, null, false, "dateDesc", 0, 100);
            Console.WriteLine($"search 'invoice'={q.Total}");

            if (all.Rows.Count > 0)
            {
                var first = all.Rows[0];
                var (msg, atts) = store.LoadMessage(first.Id);
                Console.WriteLine($"loaded id={first.Id} subject='{msg.Subject}' atts={atts.Count}");
                if (atts.Count > 0)
                {
                    var (name, bytes) = store.GetAttachment(first.Id, 0);
                    Console.WriteLine($"attachment0 '{name}' {bytes.Length} bytes");
                }

                Console.WriteLine($"trash={store.MoveToTrash(new[] { first.Id })}");
                Console.WriteLine($"afterTrash total={store.Search(null, null, null, null, null, false, "dateDesc", 0, 10).Total}");
                Console.WriteLine($"restore={store.RestoreFromTrash(new[] { first.Id })}");
                Console.WriteLine($"afterRestore total={store.Search(null, null, null, null, null, false, "dateDesc", 0, 10).Total}");
                Console.WriteLine($"trashAgain={store.MoveToTrash(new[] { first.Id })}");
                Console.WriteLine($"purged={store.PurgeTrash()}");
                Console.WriteLine($"final total={store.Search(null, null, null, null, null, false, "dateDesc", 0, 10).Total}");
                var manifest = Path.Combine(folder, MailStore.PurgeManifestName);
                Console.WriteLine(File.Exists(manifest)
                    ? $"manifest lines={File.ReadAllLines(manifest).Length}: {File.ReadAllLines(manifest)[^1]}"
                    : "manifest MISSING");
            }
            Console.WriteLine("SMOKE OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SMOKE FAIL: " + ex);
            return 1;
        }
    }
}
