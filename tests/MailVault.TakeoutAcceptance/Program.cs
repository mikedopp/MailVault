using System.IO.Compression;
using MailVault.Services;

var root = Path.Combine(Path.GetTempPath(), "MailVault-TakeoutAcceptance", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var zipPath = Path.Combine(root, "takeout-family-001.zip");
    var zipPath2 = Path.Combine(root, "takeout-family-002.zip");
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("Takeout/Mail/All mail Including Spam and Trash.mbox");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(Mbox());
    }
    using (var zip = ZipFile.Open(zipPath2, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("Takeout/Mail/Family update.eml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(Eml());
    }

    var archives = new[] { zipPath, zipPath2 };
    var preflight = TakeoutMailPreflight.Analyze(archives, root, null, CancellationToken.None);
    Assert(preflight.Archives.Count == 2 && preflight.MailEntries == 2,
        "mail multipart preflight did not inventory both parts");
    Assert(preflight.Warnings.Any(w => w.Contains("2 contiguous part", StringComparison.Ordinal)),
        "mail multipart set was not identified");

    var gapZip = Path.Combine(root, "takeout-gap-003.zip");
    File.Copy(zipPath2, gapZip);
    AssertThrows<InvalidDataException>(() =>
        TakeoutMailPreflight.Analyze(new[] { zipPath, gapZip }, root, null, CancellationToken.None),
        "mail multipart gap was accepted");
    File.Delete(gapZip);

    var resumeOutput = Path.Combine(root, "resume");
    var cancel = new CancellationTokenSource();
    AssertThrows<OperationCanceledException>(() =>
        new TakeoutMailImporter().ImportArchives(archives, "person@example.com", resumeOutput, p =>
        {
            if (p.Phase == "importing" && p.Done == 1) cancel.Cancel();
        }, cancel.Token), "cancelled mail intake unexpectedly completed");
    var resumed = new TakeoutMailImporter().ImportArchives(
        archives, "person@example.com", resumeOutput, null, CancellationToken.None);
    Assert(resumed.Added == 1 && resumed.Skipped == 2,
        $"mail resume failed: added={resumed.Added}, skipped={resumed.Skipped}, failed={resumed.Failed}, errors={string.Join(" | ", resumed.Errors)}");

    var output = Path.Combine(root, "sorted");
    var importer = new TakeoutMailImporter();
    var first = importer.ImportArchives(archives, "person@example.com", output, null, CancellationToken.None);
    Assert(first.Added == 3, $"expected 3 added messages, got {first.Added}");
    Assert(first.Failed == 0, $"expected no failures, got {first.Failed}");
    Assert(File.Exists(first.ReceiptPath), "receipt was not written");

    using var store = new MailStore();
    store.Open(first.MailRoot);
    store.IndexFolder(null, CancellationToken.None);
    var search = store.Search("invoice", null, null, null, null, false, "dateDesc", 0, 10);
    Assert(search.Total == 1, $"expected one invoice hit, got {search.Total}");

    var second = importer.ImportArchives(archives, "person@example.com", output, null, CancellationToken.None);
    Assert(second.Added == 0 && second.Skipped == 3,
        $"re-import was not idempotent: added={second.Added}, skipped={second.Skipped}");

    if (args.Length > 0 && File.Exists(args[0]))
    {
        var cliOutput = Path.Combine(root, "published-cli");
        var result = Run(args[0], "--import-takeout", root, "person@example.com", cliOutput);
        Assert(result.code == 0, $"published MailVault import failed: {result.output}");
        Assert(result.output.Contains("messages=3", StringComparison.Ordinal),
            $"published MailVault did not index three messages: {result.output}");
    }
    Console.WriteLine($"MAILVAULT TAKEOUT ACCEPTANCE OK multipart={preflight.Archives.Count} added={first.Added} " +
                      $"searchHits={search.Total} resumeAdded={resumed.Added} resumeSkipped={resumed.Skipped} " +
                      $"reimportSkipped={second.Skipped} gapRejected=true");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

static (int code, string output) Run(string executable, params string[] arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    foreach (var argument in arguments) psi.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(psi)!;
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output);
}

static string Mbox() => """
From sender@example.com Sat Jan 10 10:00:00 2026
From: Sender One <sender@example.com>
To: person@example.com
Subject: Invoice 1001
Date: Sat, 10 Jan 2026 10:00:00 +0000
Message-ID: <invoice-1001@example.com>
Content-Type: text/plain; charset=utf-8

The invoice total is 42 dollars.

From alerts@example.com Sun Feb 15 11:30:00 2026
From: Alerts <alerts@example.com>
To: person@example.com
Subject: Security notice
Date: Sun, 15 Feb 2026 11:30:00 +0000
Message-ID: <security-2002@example.com>
Content-Type: text/plain; charset=utf-8

Review the account security notice.

""";

static string Eml() => """
From: Family <family@example.com>
To: person@example.com
Subject: Family archive update
Date: Mon, 16 Feb 2026 12:00:00 +0000
Message-ID: <family-3003@example.com>
Content-Type: text/plain; charset=utf-8

Family photos are archived locally.
""";
