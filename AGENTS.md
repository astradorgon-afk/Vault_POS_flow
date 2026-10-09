# VaultFlow scope

- The web site and its browser PWA are the active product focus. Leave `src/Pos.Client` (MAUI desktop and phone) alone unless the user explicitly asks for it.
- Keep the existing server-rendered web workflows available while the offline PWA is rolled out in stages.
- An offline edit is **Saved on device** until the server accepts it. Approvals, stock availability, account changes, and other shared decisions take effect only after server confirmation.
- Cash sales and shifts are outside the offline PWA scope.
- For multi-file work, use ToolSearch to find Ruflo MCP tools when available. Check `[INTELLIGENCE]` suggestions before work.
