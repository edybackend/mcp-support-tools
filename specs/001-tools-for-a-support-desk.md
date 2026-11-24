# 001 · MCP tools for a support desk

## Context
A support team wants an AI assistant (Claude, or any MCP client) to look up tickets, triage them and leave notes. The assistant must reach the desk only through a small, well-described set of tools, and only with the rights its key grants.

## Behavior
- An MCP server over **Streamable HTTP** at `/mcp`, stateless (any instance can answer any request).
- Read tools: `search_tickets`, `get_ticket`, `customer_history`, `ticket_stats`.
- Write tools: `add_internal_note`, `set_priority`, `close_ticket`.
- Resource template `ticket://{ticketId}` (Markdown) and prompt `triage_ticket`.
- Every tool carries MCP annotations: read tools are `readOnly` and `idempotent`; `close_ticket` is `destructive` so clients ask the user first.
- Descriptions are written for the model: purpose, argument formats and examples.

## Acceptance criteria
- Results are structured (JSON with a schema) as well as text.
- Ticket ids look like `T-1001`, customer ids `C-001`; anything else is a tool error that says the expected format.
- Closing a solved ticket is a tool error; the resolution must be 10–1,000 characters.

## Out of scope
A ticketing UI, email integration, the stdio transport.
