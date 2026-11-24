# 002 · Guardrails

## Context
Tools give a model real power over real data. The server, not the model, must enforce what is allowed.

## Behavior
- **Authentication:** `Authorization: Bearer <key>` (or `X-Api-Key`). Only the SHA-256 of each key is configured. No key → `401`.
- **Scopes:** each key grants scopes: `tickets.read`, `tickets.write`, `audit.read`. Tools and resources carry `[Authorize]` policies; a client without `tickets.write` does not even see the write tools in `tools/list`, and calling one is refused.
- **Personal data:** customer emails and phones are masked in every result (`a***@example.com`, `***-4321`).
- **Input safety:** notes and resolutions that contain a card number (Luhn-valid 13–19 digits) or something that looks like a credential are refused with an explanation.
- **Rate limit:** 30 tool calls per minute per client (configurable); over the limit the call returns a tool error telling the model to wait.
- **Audit:** every tool call is stored: time, client, tool, arguments (cards and secrets replaced, long values cut), outcome, duration. `GET /audit` needs `audit.read`.

## Acceptance criteria
- The read-only key lists 4 tools; the agent key lists 7.
- A refused note is not stored, and its audit row shows `[card]` or `[secret]` instead of the value.
- The 31st call in a minute is refused; the next minute works again.

## Out of scope
OAuth 2.1 authorization server, per-tenant data isolation.
