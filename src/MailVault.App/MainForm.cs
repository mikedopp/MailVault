using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MailVault.Services;

namespace MailVault;

public sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly MailStore _store = new();
    private CancellationTokenSource? _indexCts;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MainForm()
    {
        Text = "MailVault — offline Gmail archive viewer";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        FormClosed += (_, _) => { _indexCts?.Cancel(); _store.Dispose(); };
    }

    private async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailVault"));
        await _web.EnsureCoreWebView2Async(env);
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "mailvault.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;
        _web.CoreWebView2.Navigate("https://mailvault.local/index.html");
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? req = null;
        try
        {
            req = JsonNode.Parse(e.WebMessageAsJson);
            var id = req!["id"]!.GetValue<int>();
            var cmd = req["cmd"]!.GetValue<string>();
            var args = req["args"] ?? new JsonObject();
            var data = await HandleAsync(cmd, args);
            Reply(id, ok: true, data);
        }
        catch (Exception ex)
        {
            if (req?["id"] is not null)
                Reply(req["id"]!.GetValue<int>(), ok: false, ex.Message);
        }
    }

    private void Reply(int id, bool ok, object? data)
    {
        var payload = JsonSerializer.Serialize(new { id, ok, data }, JsonOpts);
        _web.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private void Push(string ev, object data)
    {
        var payload = JsonSerializer.Serialize(new { ev, data }, JsonOpts);
        BeginInvoke(() => _web.CoreWebView2.PostWebMessageAsJson(payload));
    }

    private async Task<object?> HandleAsync(string cmd, JsonNode args)
    {
        switch (cmd)
        {
            case "pickFolder":
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Select a Gmail backup folder (contains .eml files, e.g. N:\\GoogleExport\\you@gmail.com\\Gmail)",
                    UseDescriptionForTitle = true,
                };
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
            }

            case "openFolder":
            {
                var folder = args["path"]!.GetValue<string>();
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
                _store.Open(folder);
                StartIndexing();
                return _store.Stats();
            }

            case "reindex":
                StartIndexing();
                return true;

            case "stats":
                return _store.Stats();

            case "search":
                return _store.Search(
                    args["query"]?.GetValue<string>(),
                    args["from"]?.GetValue<string>(),
                    args["dateFrom"]?.GetValue<long?>(),
                    args["dateTo"]?.GetValue<long?>(),
                    args["hasAttach"]?.GetValue<bool?>(),
                    args["deletedView"]?.GetValue<bool>() ?? false,
                    args["sort"]?.GetValue<string>() ?? "dateDesc",
                    args["offset"]?.GetValue<int>() ?? 0,
                    args["limit"]?.GetValue<int>() ?? 200);

            case "getMessage":
            {
                var id = args["msgId"]!.GetValue<long>();
                var (msg, atts) = _store.LoadMessage(id);
                return new
                {
                    subject = msg.Subject ?? "(no subject)",
                    from = msg.From?.ToString() ?? "",
                    to = msg.To?.ToString() ?? "",
                    cc = msg.Cc?.ToString() ?? "",
                    date = msg.Date == default ? "" : msg.Date.LocalDateTime.ToString("ddd, MMM d yyyy h:mm tt"),
                    messageId = msg.MessageId ?? "",
                    html = msg.HtmlBody,
                    text = msg.TextBody,
                    attachments = atts,
                };
            }

            case "openAttachment":
            {
                var (name, bytes) = _store.GetAttachment(
                    args["msgId"]!.GetValue<long>(), args["index"]!.GetValue<int>());
                var dir = Path.Combine(Path.GetTempPath(), "MailVault");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, SanitizeFileName(name));
                await File.WriteAllBytesAsync(path, bytes);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }

            case "saveAttachment":
            {
                var (name, bytes) = _store.GetAttachment(
                    args["msgId"]!.GetValue<long>(), args["index"]!.GetValue<int>());
                using var dlg = new SaveFileDialog { FileName = SanitizeFileName(name) };
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                await File.WriteAllBytesAsync(dlg.FileName, bytes);
                return true;
            }

            case "openEml":
            {
                var path = _store.GetFullPath(args["msgId"]!.GetValue<long>());
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }

            case "delete":
                return _store.MoveToTrash(IdList(args));

            case "restore":
                return _store.RestoreFromTrash(IdList(args));

            case "purgeTrash":
            {
                var stats = (dynamic)_store.Stats();
                var confirm = MessageBox.Show(this,
                    $"Permanently delete {stats.trashCount} message(s) from the archive on disk?\n\nThis cannot be undone.",
                    "Empty MailVault trash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes) return -1;
                return _store.PurgeTrash();
            }

            default:
                throw new InvalidOperationException($"unknown command: {cmd}");
        }
    }

    private static List<long> IdList(JsonNode args) =>
        args["ids"]!.AsArray().Select(n => n!.GetValue<long>()).ToList();

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "attachment" : name;
    }

    private void StartIndexing()
    {
        _indexCts?.Cancel();
        var cts = _indexCts = new CancellationTokenSource();
        Task.Run(() =>
        {
            try
            {
                var (indexed, removed) = _store.IndexFolder(
                    (done, total) => Push("indexProgress", new { done, total }), cts.Token);
                Push("indexDone", new { indexed, removed });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Push("indexError", new { message = ex.Message }); }
        }, cts.Token);
    }
}
