using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MimeKit;

namespace MailVault.Services;

public record TakeoutImportProgress(string Phase, int Done, int Total, string Current);

public record TakeoutImportResult(
    string Account,
    string MailRoot,
    string ReceiptPath,
    int Archives,
    int Mailboxes,
    int Added,
    int Skipped,
    int Failed,
    long SourceBytes,
    long MailBytes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>
/// Imports Gmail content from one or more Google Takeout ZIP files. MBOX entries
/// are converted into individual EML files under an account/date hierarchy so
/// MailVault can index them without modifying the original archives.
/// </summary>
public sealed class TakeoutMailImporter
{
    public TakeoutImportResult Import(
        string inputPath,
        string account,
        string destinationRoot,
        Action<TakeoutImportProgress>? progress,
        CancellationToken ct) =>
        ImportArchives(ResolveArchives(inputPath), account, destinationRoot, progress, ct);

    public TakeoutImportResult ImportArchives(
        IEnumerable<string> archivePaths,
        string account,
        string destinationRoot,
        Action<TakeoutImportProgress>? progress,
        CancellationToken ct)
    {
        var archives = archivePaths
            .Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (archives.Count == 0)
            throw new FileNotFoundException("No Google Takeout ZIP files were found.");

        var safeAccount = SanitizeSegment(account.Trim().ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(safeAccount))
            throw new ArgumentException("An account name or email is required.", nameof(account));

        var mailRoot = Path.Combine(Path.GetFullPath(destinationRoot), safeAccount, "Gmail");
        Directory.CreateDirectory(mailRoot);

        var preflight = TakeoutMailPreflight.Analyze(archives, mailRoot, progress, ct);

        var errors = new List<string>();
        int mailboxCount = 0, added = 0, skipped = 0, failed = 0, archiveDone = 0;

        foreach (var archivePath in archives)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(new TakeoutImportProgress(
                "opening", archiveDone, archives.Count, Path.GetFileName(archivePath)));

            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var mailEntries = archive.Entries
                    .Where(TakeoutMailPreflight.IsMailEntry)
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var entry in mailEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.FullName.EndsWith(".mbox", StringComparison.OrdinalIgnoreCase))
                        {
                            mailboxCount++;
                            using var stream = entry.Open();
                            var parser = new MimeParser(stream, MimeFormat.Mbox);
                            while (!parser.IsEndOfStream)
                            {
                                ct.ThrowIfCancellationRequested();
                                var message = parser.ParseMessage(ct);
                                ImportMessage(message, entry.LastWriteTime, mailRoot, ref added, ref skipped, ct);
                            }
                        }
                        else
                        {
                            using var stream = entry.Open();
                            var message = MimeMessage.Load(stream, ct);
                            ImportMessage(message, entry.LastWriteTime, mailRoot, ref added, ref skipped, ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        errors.Add($"{Path.GetFileName(archivePath)} :: {entry.FullName} :: {ex.Message}");
                    }
                }

                if (mailEntries.Count == 0)
                    errors.Add($"{Path.GetFileName(archivePath)} :: no Takeout Mail .mbox or .eml entries");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                errors.Add($"{Path.GetFileName(archivePath)} :: {ex.Message}");
            }

            archiveDone++;
            progress?.Invoke(new TakeoutImportProgress(
                "importing", archiveDone, archives.Count, Path.GetFileName(archivePath)));
        }

        var receiptDir = Path.Combine(mailRoot, "_TakeoutReceipts");
        Directory.CreateDirectory(receiptDir);
        var receiptPath = Path.Combine(receiptDir, $"MailVault-Takeout-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
        var result = new TakeoutImportResult(
            account.Trim(), mailRoot, receiptPath, archives.Count, mailboxCount,
            added, skipped, failed, preflight.ArchiveBytes, preflight.MailBytes,
            preflight.Warnings, errors);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            schema = 2,
            importedAtUtc = DateTimeOffset.UtcNow,
            result.Account,
            result.MailRoot,
            archives = preflight.Archives.Select(a => new
            {
                a.Name,
                a.ArchiveBytes,
                a.MailBytes,
                a.MailEntries,
                a.Sha256,
                a.SetName,
                a.PartNumber,
            }).ToArray(),
            result.Mailboxes,
            result.Added,
            result.Skipped,
            result.Failed,
            result.SourceBytes,
            result.MailBytes,
            result.Warnings,
            result.Errors,
        }, new JsonSerializerOptions { WriteIndented = true }));

        progress?.Invoke(new TakeoutImportProgress("done", archives.Count, archives.Count, receiptPath));
        return result;
    }

    private static List<string> ResolveArchives(string inputPath)
    {
        if (File.Exists(inputPath))
            return Path.GetExtension(inputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { Path.GetFullPath(inputPath) }
                : new List<string>();

        if (!Directory.Exists(inputPath)) return new List<string>();
        return Directory.EnumerateFiles(inputPath, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static void ImportMessage(
        MimeMessage message,
        DateTimeOffset archiveTime,
        string mailRoot,
        ref int added,
        ref int skipped,
        CancellationToken ct)
    {
        var date = Plausible(message.Date) ? message.Date : archiveTime;
        var bucket = Plausible(date)
            ? Path.Combine("Takeout", date.UtcDateTime.ToString("yyyy"), date.UtcDateTime.ToString("MM"))
            : Path.Combine("Takeout", "Undated");
        var dir = Path.Combine(mailRoot, bucket);
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".mailvault-{Guid.NewGuid():N}.partial");
        string hash;
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1024 * 1024, FileOptions.SequentialScan))
                message.WriteTo(output, ct);
            using (var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read,
                       1024 * 1024, FileOptions.SequentialScan))
            {
                using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    incremental.AppendData(buffer, 0, read);
                }
                hash = Convert.ToHexString(incremental.GetHashAndReset());
            }

            var stamp = Plausible(date) ? date.UtcDateTime.ToString("yyyyMMddTHHmmssZ") : "undated";
            var path = Path.Combine(dir, $"{stamp}_{hash[..16]}.eml");
            if (File.Exists(path))
            {
                skipped++;
                return;
            }

            File.Move(temp, path);
            if (Plausible(date)) File.SetLastWriteTimeUtc(path, date.UtcDateTime);
            added++;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool Plausible(DateTimeOffset date) =>
        date != default && date.Year >= 1990 && date <= DateTimeOffset.UtcNow.AddDays(2);

    private static string SanitizeSegment(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Trim().TrimEnd('.');
    }
}
