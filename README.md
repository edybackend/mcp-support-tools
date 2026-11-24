# MCP Support Tools · Guardrails for AI Agents

[![CI](https://github.com/edybackend/mcp-support-tools/actions/workflows/ci.yml/badge.svg)](https://github.com/edybackend/mcp-support-tools/actions/workflows/ci.yml)
[Leia em português](README.pt-BR.md)

A **Model Context Protocol** server in C# (.NET 10, official MCP SDK) that gives an AI assistant tools over a customer
support desk: search tickets, read them, triage, add notes, close. Claude, or any MCP client, can call them.

The point is not the tools; it is **what the server enforces** when a model calls them:

| Guardrail | How |
|---|---|
| Who may connect | API keys, configured only as SHA-256 fingerprints, compared in constant time; no key → `401` |
| What each client may do | scopes per key (`tickets.read`, `tickets.write`, `audit.read`) as `[Authorize]` policies on tools: a read-only client **does not even see** the write tools |
| What the model is told | MCP annotations on every tool (`readOnly`, `destructive`, `idempotent`): `close_ticket` is destructive, so clients ask the user first |
| What the model sees | emails and phones masked in every result (`a***@example.com`, `***-4321`) |
| What the model may write | notes with card numbers (Luhn-checked) or credentials are refused, with an explanation |
| How much | 30 tool calls per minute per client; a looping agent gets a tool error, not your database |
| What happened | every call audited: client, tool, redacted arguments, outcome, duration |

## Quick start

Requirements: Docker.

```bash
docker compose up --build        # MCP endpoint: http://localhost:8080/mcp (Streamable HTTP)
```

Demo keys (public on purpose; only their SHA-256 is in the configuration):

| Key | Client | Scopes |
|---|---|---|
| `dev-readonly-key-change-me` | readonly-assistant | `tickets.read` |
| `dev-agent-key-change-me` | support-agent | `tickets.read`, `tickets.write`, `audit.read` |

**With Claude Code**: this folder has a [`.mcp.json`](.mcp.json) (read-only key), so `claude` started here offers the
`support-desk` server. To add it elsewhere, with write access:

```bash
claude mcp add --transport http support-desk http://localhost:8080/mcp \
  --header "Authorization: Bearer dev-agent-key-change-me"
```

Then ask: *"Triage the most urgent open ticket"*. The `triage_ticket` prompt describes the steps the desk expects.

**With raw JSON-RPC** (what any client sends):

```bash
curl -s localhost:8080/mcp -H 'Authorization: Bearer dev-readonly-key-change-me' \
  -H 'content-type: application/json' -H 'accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_ticket","arguments":{"ticketId":"T-1001"}}}'
```

The audit log: `curl localhost:8080/audit -H 'Authorization: Bearer dev-agent-key-change-me'`.

<details>
<summary>Without Docker (.NET 10 SDK)</summary>

```bash
dotnet run --project src/SupportDesk.Mcp   # http://localhost:8080/mcp
```
</details>

## What the server offers

| Kind | Name | Scope | Annotations |
|---|---|---|---|
| tool | `search_tickets` (text, status, priority, limit) | read | read-only, idempotent |
| tool | `get_ticket` (ticketId) | read | read-only, idempotent |
| tool | `customer_history` (customerId) | read | read-only, idempotent |
| tool | `ticket_stats` | read | read-only, idempotent |
| tool | `add_internal_note` (ticketId, note) | write | not idempotent |
| tool | `set_priority` (ticketId, priority) | write | idempotent |
| tool | `close_ticket` (ticketId, resolution) | write | **destructive** |
| resource | `ticket://{ticketId}` (Markdown) | read | |
| prompt | `triage_ticket` (ticketId) | read | |

Results are structured JSON with a schema. Rule violations come back as tool errors written for the model:
`"ticketId looks like T-1001."`, `"Ticket T-1007 is already solved."`, `"The note contains what looks like a card number…"`.

## How it works

```mermaid
flowchart LR
    C[MCP client<br/>Claude, IDE, agent] -->|POST /mcp + Bearer key| A[API key auth<br/>→ client + scopes]
    A --> F[MCP authorization filter<br/>hides / refuses tools by scope]
    F --> G[Tool call guard<br/>rate limit → call → audit]
    G --> T[Tools / resource / prompt]
    T --> D[Support desk<br/>rules + masked views]
    D --> DB[(SQLite)]
    G --> L[(audit_log)]
```

The transport is **stateless Streamable HTTP**, so any instance behind a load balancer can answer any request.
Authentication is ordinary ASP.NET Core; the MCP SDK's authorization filters turn `[Authorize(Policy = …)]` on a tool
class into both "not listed" and "refused".

## How it is tested

The integration tests connect with the **official MCP C# client** over Streamable HTTP to the real server:

| Behavior | Test |
|---|---|
| No key → connection refused | `Without_a_key_the_connection_is_refused` |
| Read-only key lists 4 tools, agent key 7 | `A_read_only_key_sees_only_the_read_tools` |
| Annotations as declared | `Annotations_tell_the_client_which_tools_are_safe` |
| Read-only key calling a write tool → forbidden | `A_read_only_key_cannot_call_a_write_tool` |
| Email and phone masked | `Personal_data_is_masked_in_results` |
| Card number / password in a note → refused and not stored | `Notes_with_card_data_or_credentials_are_refused_and_not_stored` |
| Errors the model can act on | `Rule_violations_come_back_as_errors_the_model_can_act_on` |
| 31st call in a minute refused; next minute works (clock moved) | `The_31st_call_in_a_minute_is_refused_and_the_next_minute_works` |
| Audit rows, with `[card]` instead of the number; `/audit` needs `audit.read` | `Every_call_is_audited_with_redacted_arguments` |
| Resource and prompt | `The_ticket_resource_and_the_triage_prompt_are_available` |

CI also starts the container and speaks raw JSON-RPC to it (401, initialize, 4 vs 7 tools, masked email, refused card).
Removing `AddAuthorizationFilters()` makes 12 of 13 integration tests fail: the SDK refuses to serve `[Authorize]` tools
without the filter, which is secure by default.

## Tech stack

| Area | Choice |
|---|---|
| Protocol | Model Context Protocol, `ModelContextProtocol.AspNetCore` 2.4 (official C# SDK), Streamable HTTP, stateless |
| Back-end | .NET 10, ASP.NET Core authentication and authorization policies, Dapper + SQLite |
| Quality | xUnit v3 with the official MCP client, Roslyn analyzers (warnings as errors), gitleaks |
| Delivery | non-root container, Docker Compose, GitHub Actions |

## Design decisions

- **Guardrails in the server, not in the prompt.** A system prompt can be ignored or injected; a policy on the tool cannot.
- **Least privilege by listing.** A client that cannot write does not see write tools, so the model never plans with them.
- **Annotations are honest.** They are hints to the client, not security; the scopes are the security. Both are set, and a hook makes them mandatory.
- **Errors are instructions.** A model recovers from `"ticketId looks like T-1001"`; it cannot recover from `"Bad Request"`.
- **Masking at the source.** The desk returns masked views, so no tool can leak what it never receives.
- **Stateless HTTP.** No session affinity; the server scales like any API.

## Known trade-offs

- **API keys, not OAuth.** The MCP spec defines OAuth 2.1 for remote servers; keys keep the demo self-contained. The SDK supports the OAuth flow when a real identity provider is available.
- **The rate limit is per instance** (in memory). Several instances would share it through Redis or the gateway.
- **Pattern-based detection** of cards and secrets catches the common cases, not all of them; a DLP service would go further.
- **Denied calls are refused by the authorization filter before the audit filter**, so they appear in the ASP.NET Core logs, not in `audit_log`.

## Working with an AI agent

This repository is a tool for AI agents and was also built with one. Its own hooks in [`.claude/`](.claude) require every MCP tool to declare its annotations and description, every MCP type to carry an authorization policy, and `AddAuthorizationFilters()` to stay in place. [`CLAUDE.md`](CLAUDE.md) explains the rules for the MCP surface.

## License

[MIT](LICENSE)
