using MailVault.Services;

namespace MailVault;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--smoke")
            return Smoke(args[1]);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>Headless exercise of the full store pipeline against a folder of .eml files.</summary>
    private static int Smoke(string folder)
    {
        try
        {
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
