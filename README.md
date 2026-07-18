# MailVault

Offline viewer + cleanup tool for Gmail backups made with GYB (see the
GoogleExit repo). Point it at a folder of `.eml` files and get full-text
search, attachment viewing, and reversible cleanup — no Google, no internet.

WinForms + WebView2 host, vanilla-JS dark/gold UI, SQLite FTS5 index.

## Run

```powershell
dotnet run --project src\MailVault.App
# or build once and run bin\Debug\net8.0-windows\MailVault.exe
```

Click **Open backup folder…** and pick e.g. `N:\GoogleExport\you@gmail.com\Gmail`.
First open indexes every message (progress bar; ~50k messages takes a few
minutes); after that it's instant and incremental.

## Features

- **Search everything** — subject, sender, recipients, body text, attachment
  names — via SQLite FTS5. Filters: sender, date range, has-attachments.
  Sort by date, sender, subject, or size.
- **Read messages** — HTML mail renders in a sandboxed iframe (scripts
  blocked); plain text otherwise. Open the raw `.eml` in your mail client
  any time.
- **Attachments** — listed as chips; open with the default app or Save As.
- **Cleanup CRUD** — select messages → Delete moves the `.eml` into
  `_MailVaultTrash` (inside the backup folder, original paths preserved).
  Trash view lets you Restore. **Empty trash** permanently deletes from disk
  after a confirmation dialog. Nothing is ever modified in place.

## Design notes

- The index lives at `<backup>\MailVault.index.db`. Delete it to force a full
  re-index; it is ignored by the indexer itself.
- GYB's own `msg-db.sqlite` is **never touched**, so GYB backup/resume keeps
  working. But note: if you purge messages here and later run `gyb --action
  backup` again, GYB may re-download the purged messages (they still exist on
  the server until deleted there). To prevent that, sync your purges to the
  live account: every Empty-trash appends the Message-IDs to
  `_PurgedFromArchive.jsonl`, and GoogleExit's `Invoke-GmailSyncDeletes.ps1`
  applies them server-side (dry-run + typed confirmation).
- Headless smoke test: `MailVault.exe --smoke <folder-of-eml>` exercises
  index/search/load/attachment/trash/restore/purge and prints SMOKE OK.
