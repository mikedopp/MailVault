using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MailVault.Services;

public record TakeoutMailArchiveInfo(
    string Path,
    string Name,
    long ArchiveBytes,
    long MailBytes,
    int MailEntries,
    string Sha256,
    string SetName,
    int? PartNumber);

public record TakeoutMailPreflightResult(
    IReadOnlyList<TakeoutMailArchiveInfo> Archives,
    long ArchiveBytes,
    long MailBytes,
    int MailEntries,
    long AvailableBytes,
    IReadOnlyList<string> Warnings);

public static partial class TakeoutMailPreflight
{
    public static TakeoutMailPreflightResult Analyze(
        IEnumerable<string> archivePaths,
        string destinationRoot,
        Action<TakeoutImportProgress>? progress,
        CancellationToken ct)
    {
        var paths = archivePaths
            .Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) throw new FileNotFoundException("No Google Takeout ZIP files were found.");

        var infos = new List<TakeoutMailArchiveInfo>(paths.Count);
        for (var index = 0; index < paths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var path = paths[index];
            var name = Path.GetFileName(path);
            progress?.Invoke(new TakeoutImportProgress("preflight", index, paths.Count, $"Hashing {name}"));
            var sha = HashFile(path, ct);

            List<ZipArchiveEntry> mail;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                mail = zip.Entries.Where(IsMailEntry).ToList();
                var (setName, part) = ParsePart(name);
                infos.Add(new TakeoutMailArchiveInfo(path, name, new FileInfo(path).Length,
                    mail.Sum(e => e.Length), mail.Count, sha, setName, part));
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"Takeout ZIP is damaged or incomplete: {name}. {ex.Message}", ex);
            }
            progress?.Invoke(new TakeoutImportProgress("preflight", index + 1, paths.Count, $"Verified {name}"));
        }

        var warnings = ValidateParts(infos);
        var mailBytes = infos.Sum(i => i.MailBytes);
        var available = AvailableBytes(destinationRoot);
        var reserve = Math.Max(512L * 1024 * 1024, mailBytes / 10);
        if (available >= 0 && mailBytes + reserve > available)
            throw new IOException($"Not enough destination space for Gmail. Need {FormatBytes(mailBytes + reserve)} " +
                                  $"including reserve; available {FormatBytes(available)}.");

        return new TakeoutMailPreflightResult(infos, infos.Sum(i => i.ArchiveBytes),
            mailBytes, infos.Sum(i => i.MailEntries), available, warnings);
    }

    internal static bool IsMailEntry(ZipArchiveEntry entry)
    {
        if (entry.Length == 0 || entry.FullName.EndsWith('/')) return false;
        var normalized = entry.FullName.Replace('\\', '/');
        var isMailProduct = normalized.Contains("/Mail/", StringComparison.OrdinalIgnoreCase)
                            || normalized.StartsWith("Mail/", StringComparison.OrdinalIgnoreCase);
        return isMailProduct &&
               (normalized.EndsWith(".mbox", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".eml", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ValidateParts(IReadOnlyList<TakeoutMailArchiveInfo> archives)
    {
        var warnings = new List<string>();
        foreach (var group in archives.Where(a => a.PartNumber.HasValue)
                     .GroupBy(a => a.SetName, StringComparer.OrdinalIgnoreCase))
        {
            var parts = group.Select(a => a.PartNumber!.Value).Distinct().Order().ToArray();
            if (parts[0] != 1)
                throw new InvalidDataException($"Multipart Takeout set '{group.Key}' is missing part 001.");
            var missing = Enumerable.Range(1, parts[^1]).Except(parts).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"Multipart Takeout set '{group.Key}' is incomplete. Missing: " +
                    string.Join(", ", missing.Select(p => p.ToString("000"))) + ".");
            warnings.Add($"Detected multipart set '{group.Key}' with {parts.Length} contiguous part(s).");
        }
        return warnings;
    }

    private static (string setName, int? part) ParsePart(string name)
    {
        var match = MultipartName().Match(name);
        return match.Success
            ? (match.Groups["set"].Value, int.Parse(match.Groups["part"].Value))
            : (Path.GetFileNameWithoutExtension(name), null);
    }

    private static long AvailableBytes(string destinationRoot)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
        if (string.IsNullOrWhiteSpace(root)) return -1;
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch { return -1; }
    }

    private static string HashFile(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 * 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    [GeneratedRegex(@"^(?<set>.+)-(?<part>\d{3})\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartName();
}
