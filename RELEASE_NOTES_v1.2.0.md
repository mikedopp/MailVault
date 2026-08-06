# MailVault v1.2.0

MailVault now imports Google Takeout ZIP sets directly and turns Gmail MBOX or
EML content into an offline, searchable archive.

## Added

- Multi-part Takeout intake with missing-part detection.
- SHA-256 source proof, expanded-mail byte counts, and JSON receipts.
- Destination free-space preflight with a safety reserve.
- Restart-safe imports using stable content-derived EML names.
- Streaming message serialization to avoid a second in-memory EML copy.
- `--import-takeout <zip-or-folder> <account> <archive-parent>` CLI workflow.
- Arctic Steel operator status for large Takeouts, dependencies, and diagnostics.

## QA

- Two-part intake, missing-part rejection, cancellation/restart, idempotent
  re-import, MBOX splitting, published CLI import, and FTS search passed.
- Valhalla style and security checks: zero failures and zero warnings.
- Self-contained `win-x64` build and published WebView assets verified.

```text
MAILVAULT TAKEOUT ACCEPTANCE OK multipart=2 added=3 searchHits=1
resumeAdded=1 resumeSkipped=2 reimportSkipped=3 gapRejected=true
```

## Install

Extract `MailVault-v1.2.0-win-x64.zip` and run `MailVault.exe`. Keep the included
`wwwroot` folder beside the EXE. No .NET installation or Google credentials are
required.

Real family exports above 5 GB remain the next bounded QA target. The importer
is streaming and size-independent, but that exact real-data scenario is not
claimed as proven by the synthetic test.
