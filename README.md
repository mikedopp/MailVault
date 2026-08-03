# MailVault

Offline viewer + cleanup tool for Gmail backups made with GYB (see the
GoogleExit repo). Point it at a folder of `.eml` files and get full-text
search, attachment viewing, and reversible cleanup — no Google, no internet.

WinForms + WebView2 host, vanilla-JS dark/gold UI, SQLite FTS5 index.

## Run

```powershell
# open an archive directly
MailVault.exe "N:\GoogleExport\you@gmail.com\Gmail"

# or from source
dotnet run --project src\MailVault.App
```

With no argument, click **Open backup folder…** and pick e.g. `N:\GoogleExport\you@gmail.com\Gmail`.
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

## Dates in real mail

Archived mail contains `Date:` headers that are missing entirely or plainly
fabricated — this archive held spam stamped **year 2611** and messages with no
date at all, which landed at the 1969 Unix epoch. Both wrecked chronological
browsing.

The header value is stored and displayed unchanged, because for a records
archive the header *is* the evidence. Sorting uses a separate `sortDate` that
falls back to the file's own timestamp whenever the header is missing or
outside 1990..now. Those rows show a `~` prefix in muted text, so an inferred
date is never mistaken for a real one.

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
## Headless modes

```powershell
# Read-only: index an archive and report what's in it. Safe on real mail.
MailVault.exe --scan "N:\GoogleExport\you@gmail.com\Gmail"

# Destructive self-test on a throwaway fixture (trashes + purges a message)
MailVault.exe --smoke <folder-of-eml>
```

`--smoke` **permanently deletes a message**, so it refuses to run against any
folder holding more than 100 `.eml` files. Use `--scan` for real archives.
(`--force` overrides, but there's rarely a good reason.)

Note: this is a WinForms exe, so PowerShell won't wait for it or capture its
output unless you pipe it — use `| Out-String`, or `$LASTEXITCODE` will be
meaningless and you'll see no output at all.
