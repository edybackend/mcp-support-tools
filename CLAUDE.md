# Engineering rules for AI agents

This file is loaded by Claude Code in every session. It defines how changes are made in this repository.
Hooks in `.claude/settings.json` enforce the parts that can be automated.

## Workflow
1. **Spec first.** A new behavior starts as a spec in `specs/NNN-name.md` (context, behavior, acceptance criteria, out of scope).
2. **Small steps.** One concern per commit, [Conventional Commits](https://www.conventionalcommits.org/) (`feat(mcp): ...`, `fix(security): ...`).
3. **Definition of done** = the CI gates pass locally: `dotnet build` (warnings are errors), `dotnet test`, `dotnet format --verify-no-changes`. The Stop hook blocks finishing while they are red.

## Layout
- `src/SupportDesk.Mcp/Desk`: the support desk itself (data, rules). It returns views that are already safe for an AI client.
- `src/SupportDesk.Mcp/Mcp`: the MCP surface (tools, resource, prompt). Thin: argument descriptions and a call into `Desk`.
- `src/SupportDesk.Mcp/Security`: API keys and scopes, redaction, the tool call guard (rate limit + audit).

## Rules for the MCP surface
- **Every tool declares its safety**: `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`. Anything that changes data is `ReadOnly = false`; anything hard to undo is `Destructive = true`. Hook-enforced.
- **Every tool, resource and prompt type has `[Authorize(Policy = Scopes.…)]`**, and `AddAuthorizationFilters()` stays in `Program.cs`. Hook-enforced. Write tools require `tickets.write`.
- **Descriptions are prompts.** Say what the tool is for, when to use it, and the exact argument format with an example. Errors must tell the model how to recover ("ticketId looks like T-1001").
- **Never return raw personal data.** Views mask emails and phones (`Redaction`). Never accept card numbers or credentials in free text.
- **Keep results small and structured** (`UseStructuredContent = true`, limits on lists).
- New tools get an integration test through the real MCP client (`tests/SupportDesk.IntegrationTests`).

## Security
- API keys are configured only as SHA-256 fingerprints; compare in constant time.
- The rate limit and the audit log wrap every tool call; do not bypass `ToolCallGuard`.
- The demo keys (`dev-…-change-me`) are public on purpose. Real deployments put fingerprints in a secret store.

## C# conventions
- Classes `sealed` by default; file-scoped namespaces; `TimeProvider` for time; logging with `[LoggerMessage]`.

## Never
- Edit the SQLite files by hand. Blocked by hook.
- Read or write secrets (`.env`, keys). Blocked by permissions and hook.
- Disable an analyzer or a test to make the build pass.
- Add a dependency without a reason stated in the commit message.
