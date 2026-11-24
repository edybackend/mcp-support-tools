using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SupportDesk.Mcp.Desk;

namespace SupportDesk.Mcp.Security;

public sealed class ToolCallOptions
{
    public const string SectionName = "ToolCalls";

    /// <summary>Tool calls one client may make per minute. An agent stuck in a loop hits this instead of the database.</summary>
    public int PerClientPerMinute { get; set; } = 30;
}

/// <summary>
/// Wraps every tool call: per-client rate limit, then the call, then one audit row (client, tool, redacted
/// arguments, outcome, duration). Rule violations come back to the model as tool errors it can read and correct.
/// </summary>
public sealed partial class ToolCallGuard(SupportDatabase database, TimeProvider clock, Microsoft.Extensions.Options.IOptions<ToolCallOptions> options, ILogger<ToolCallGuard> logger)
{
    // Readable audit text ("Settings > Data", not "Settings \u003E Data"); it is stored and shown as JSON, never as HTML.
    private static readonly JsonSerializerOptions AuditJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var client = context.User?.Identity?.Name ?? "anonymous";
        var tool = context.Params?.Name ?? "?";
        var arguments = Redaction.ForAudit(JsonSerializer.Serialize(context.Params?.Arguments ?? new Dictionary<string, JsonElement>(), AuditJson));
        var started = Stopwatch.GetTimestamp();

        if (!TryTake(client))
        {
            await AuditAsync(client, tool, arguments, "rate_limited", null, started);
            return Error($"Rate limit: at most {options.Value.PerClientPerMinute} tool calls per minute for this client. Wait before calling again.");
        }

        CallToolResult result;
        try
        {
            result = await next(context, ct);
        }
        catch (DeskRuleException ex)
        {
            await AuditAsync(client, tool, arguments, "error", ex.Message, started);
            return Error(ex.Message);
        }

        var detail = result.IsError == true ? Redaction.ForAudit(string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))) : null;
        await AuditAsync(client, tool, arguments, result.IsError == true ? "error" : "ok", detail, started);
        return result;
    }

    private bool TryTake(string client)
    {
        var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var window = _windows.AddOrUpdate(client, _ => new Window(minute, 1), (_, w) => w.Minute == minute ? w with { Count = w.Count + 1 } : new Window(minute, 1));
        return window.Count <= options.Value.PerClientPerMinute;
    }

    private async Task AuditAsync(string client, string tool, string arguments, string outcome, string? detail, long started)
    {
        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await using var db = await database.OpenAsync();
        await db.ExecuteAsync(
            "INSERT INTO audit_log (at, client, tool, arguments, outcome, detail, duration_ms) VALUES (@at, @client, @tool, @arguments, @outcome, @detail, @elapsed)",
            new { at = clock.GetUtcNow().ToString("O"), client, tool, arguments, outcome, detail, elapsed });
        LogCall(client, tool, outcome, elapsed);
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool call {Tool} by {Client}: {Outcome} in {ElapsedMs} ms")]
    private partial void LogCall(string client, string tool, string outcome, long elapsedMs);

    private sealed record Window(long Minute, int Count);
}
