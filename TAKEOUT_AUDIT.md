# MailVault Google Takeout Audit

Collected: `2026-08-06T22:35:14Z` (UTC)

## Verdict

Collection status: `PARTIAL`

The original checkout could not accept a Google Takeout ZIP. It only enumerated
loose `.eml` files, while Gmail Takeout normally supplies one or more `.mbox`
entries. The implemented v1.2.0 path now reads selected ZIP parts without
changing them, rejects detected multipart gaps, checks destination space, hashes
each source ZIP, converts Mail MBOX/EML entries into dated EML files without a
second in-memory serialized copy, writes a JSON receipt, opens the resulting
Gmail folder, and indexes it.

## Findings

| Claim | Classification | Confidence | Evidence |
|---|---|---:|---|
| Baseline MailVault accepted Takeout ZIPs | `NOT_SUPPORTED` | High | `MainForm` only exposed `FolderBrowserDialog`; `MailStore.IndexFolder` enumerated `*.eml`; no ZIP/MBOX reader existed. |
| Implemented intake preserves source archives | `PROVEN` | High | Import reads `ZipArchiveEntry` streams and writes only beneath the chosen destination. Acceptance re-read the original ZIP twice. |
| MBOX and EML messages become searchable MailVault records | `PROVEN` | High | Two-part synthetic intake produced three EML files; the SQLite FTS search returned one `invoice` hit. |
| Multipart gaps are blocked | `PROVEN` | High | A selected `001` plus `003` set threw `InvalidDataException` before import. |
| Cancellation and restart preserve progress | `PROVEN` | High | Cancellation after part 001 left two messages; restart skipped those two and added the remaining message. |
| Re-import is idempotent | `PROVEN` | High | Second complete import added zero and skipped all three messages by content-derived filename. |
| Source proof is durable | `PROVEN` | High | Receipt schema 2 records each ZIP's SHA-256, compressed bytes, mail bytes, entry count, set name, and part number. |
| Published app contains the required ZIP action and operator details | `PROVEN` | High | Self-contained publish, Valhalla smoke, and rendered browser inspection of published `wwwroot`. |
| Every real-world Takeout variant is handled | `UNKNOWN` | Low | No user-supplied production Takeout set was available. |

## Validation receipts

```text
dotnet run --project tests\MailVault.TakeoutAcceptance\MailVault.TakeoutAcceptance.csproj -c Release -- <published MailVault.exe>
MAILVAULT TAKEOUT ACCEPTANCE OK multipart=2 added=3 searchHits=1 resumeAdded=1 resumeSkipped=2 reimportSkipped=3 gapRejected=true

Valhalla style check: 0 fail, 0 warn
Valhalla security check: 0 fail, 0 warn, 0 info
Build succeeded: 0 warnings, 0 errors
Published: src\MailVault.App\publish\MailVault.exe
```

## Remaining proof

Run one read-only import against a copied real Takeout ZIP set larger than 5 GB.
Compare receipt hashes with an independent SHA-256 pass, message counts against
an independent MBOX count, receipt failures, peak working set, and free-space use.
