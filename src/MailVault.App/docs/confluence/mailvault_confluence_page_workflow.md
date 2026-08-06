# Confluence Page Build Workflow

Page title:

`MailVault`

Page body:

`docs\confluence\mailvault_confluence_draft.md`

Publish packet:

`docs\confluence\mailvault_confluence_publish_packet.json`

Target:

- Confluence base URL: `https://amwins.atlassian.net/wiki`
- Space ID: `3149234181`
- Parent page ID: `3149234224`
- Status: draft first

## Build Steps

1. Open `\ConfluenceForge\publish\ConfluenceForge.exe`.
2. Confirm the credential slot is `confluence-api`.
3. Create a new page with title `MailVault`.
4. Use parent page ID `3149234224`.
5. Paste `docs\confluence\mailvault_confluence_draft.md` into Free Text / Paste.
6. Click Generate Markup.
7. Review the rendered preview.
8. Attach reviewed GIFs from `docs\media\gifs\`.
9. Create the page as a draft.
10. Leave it draft until Mike explicitly approves publishing.

## GIF Attachment Plan

| GIF | Section |
| --- | --- |
| `mailvault-launch-dependencies.gif` | Launch And Dependencies |
| `mailvault-workflow-preview.gif` | Workflow Preview |
| `mailvault-operator-receipt.gif` | Receipt And Diagnostics |
