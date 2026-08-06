# MailVault

Offline Gmail and Google Takeout mail archive viewer.

## Deliverables

| File | Purpose |
| --- | --- |
| `src\` or app project folder | C# operator app source. |
| `wwwroot\` | Portable web UI assets. |
| `README.md` | Operator and repo handoff summary. |
| `docs\confluence\mailvault_confluence_draft.md` | Confluence-ready draft page body. |
| `docs\confluence\mailvault_confluence_page_workflow.md` | Draft publish workflow for ConfluenceForge. |
| `docs\confluence\mailvault_confluence_publish_packet.json` | Draft publish packet and GIF attachment plan. |
| `docs\media\gifs\` | Reviewed operator GIF captures. |
| `REPO_NOTES.md` | Azure Repo and doppmike GitHub handoff notes. |

## Build

```powershell
pwsh -NoProfile -File \Valhalla\validators\Invoke-ValhallaStyleCheck.ps1 -ProjectPath .
pwsh -NoProfile -File \Valhalla\validators\Invoke-ValhallaBuild.ps1 -ProjectPath . -Publish
pwsh -NoProfile -File \Valhalla\validators\Invoke-ValhallaSmoke.ps1 -PublishPath <publish path>
```

## Confluence

Use `\ConfluenceForge\publish\ConfluenceForge.exe` and create the page as draft only. GIF workflows remain draft until Mike explicitly approves promotion.
