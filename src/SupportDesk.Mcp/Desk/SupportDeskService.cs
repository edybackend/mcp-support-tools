using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using SupportDesk.Mcp.Security;

namespace SupportDesk.Mcp.Desk;

public sealed record TicketSummary(string Id, string Subject, string Status, string Priority, string Customer, DateTimeOffset UpdatedAt);

public sealed record CustomerView(string Id, string Name, string Email, string Phone, string Plan);

public sealed record NoteView(string Author, string Text, DateTimeOffset CreatedAt);

public sealed record TicketDetail(
    string Id, string Subject, string Body, string Status, string Priority, string? Resolution,
    CustomerView Customer, IReadOnlyList<NoteView> Notes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record DeskStats(IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> OpenByPriority, string? OldestOpenTicket);

/// <summary>A rule of the desk was broken; the message is written for the AI client to read and act on.</summary>
public sealed class DeskRuleException(string message) : Exception(message);

/// <summary>The support desk. Everything it returns is already safe to show to an AI client (personal data masked).</summary>
public sealed partial class SupportDeskService(SupportDatabase database, TimeProvider clock)
{
    public static readonly string[] Statuses = ["open", "pending", "solved"];
    public static readonly string[] Priorities = ["low", "normal", "high", "urgent"];

    public async Task<IReadOnlyList<TicketSummary>> SearchAsync(string? text, string? status, string? priority, int limit, CancellationToken ct)
    {
        Require(status is null || Statuses.Contains(status), $"status must be one of: {string.Join(", ", Statuses)}.");
        Require(priority is null || Priorities.Contains(priority), $"priority must be one of: {string.Join(", ", Priorities)}.");
        await using var db = await database.OpenAsync(ct);
        var rows = await db.QueryAsync<SummaryRow>(
            """
            SELECT t.id AS Id, t.subject AS Subject, t.status AS Status, t.priority AS Priority, c.name AS Customer, t.updated_at AS UpdatedAt
            FROM tickets t JOIN customers c ON c.id = t.customer_id
            WHERE (@text IS NULL OR t.subject LIKE '%' || @text || '%' OR t.body LIKE '%' || @text || '%')
              AND (@status IS NULL OR t.status = @status)
              AND (@priority IS NULL OR t.priority = @priority)
            ORDER BY CASE t.priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END, t.updated_at DESC
            LIMIT @limit
            """,
            new { text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(), status, priority, limit = Math.Clamp(limit, 1, 25) });
        return [.. rows.Select(r => new TicketSummary(r.Id, r.Subject, r.Status, r.Priority, r.Customer, Parse(r.UpdatedAt)))];
    }

    public async Task<TicketDetail> GetAsync(string ticketId, CancellationToken ct)
    {
        RequireTicketId(ticketId);
        await using var db = await database.OpenAsync(ct);
        var t = await db.QuerySingleOrDefaultAsync<TicketRow>(
            """
            SELECT t.id AS Id, t.subject AS Subject, t.body AS Body, t.status AS Status, t.priority AS Priority, t.resolution AS Resolution,
                   t.created_at AS CreatedAt, t.updated_at AS UpdatedAt, c.id AS CustomerId, c.name AS Name, c.email AS Email, c.phone AS Phone, c.plan AS Plan
            FROM tickets t JOIN customers c ON c.id = t.customer_id WHERE t.id = @ticketId
            """,
            new { ticketId }) ?? throw new DeskRuleException($"Ticket {ticketId} does not exist. Use search_tickets to find ticket ids.");
        var notes = await db.QueryAsync<(string Author, string Text, string CreatedAt)>(
            "SELECT author, text, created_at FROM ticket_notes WHERE ticket_id = @ticketId ORDER BY created_at", new { ticketId });
        return new TicketDetail(
            t.Id, t.Subject, t.Body, t.Status, t.Priority, t.Resolution,
            new CustomerView(t.CustomerId, t.Name, Redaction.MaskEmail(t.Email), Redaction.MaskPhone(t.Phone), t.Plan),
            [.. notes.Select(n => new NoteView(n.Author, n.Text, Parse(n.CreatedAt)))],
            Parse(t.CreatedAt),
            Parse(t.UpdatedAt));
    }

    public async Task<IReadOnlyList<TicketSummary>> CustomerHistoryAsync(string customerId, CancellationToken ct)
    {
        Require(CustomerIdPattern().IsMatch(customerId ?? string.Empty), "customerId looks like C-001.");
        await using var db = await database.OpenAsync(ct);
        var rows = await db.QueryAsync<SummaryRow>(
            """
            SELECT t.id AS Id, t.subject AS Subject, t.status AS Status, t.priority AS Priority, c.name AS Customer, t.updated_at AS UpdatedAt
            FROM tickets t JOIN customers c ON c.id = t.customer_id WHERE c.id = @customerId ORDER BY t.created_at DESC
            """,
            new { customerId });
        return [.. rows.Select(r => new TicketSummary(r.Id, r.Subject, r.Status, r.Priority, r.Customer, Parse(r.UpdatedAt)))];
    }

    public async Task<DeskStats> StatsAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var byStatus = (await db.QueryAsync<(string Status, int Count)>("SELECT status, COUNT(*) FROM tickets GROUP BY status"))
            .ToDictionary(r => r.Status, r => r.Count);
        var open = (await db.QueryAsync<(string Priority, int Count)>("SELECT priority, COUNT(*) FROM tickets WHERE status <> 'solved' GROUP BY priority"))
            .ToDictionary(r => r.Priority, r => r.Count);
        var oldest = await db.ExecuteScalarAsync<string?>("SELECT id FROM tickets WHERE status <> 'solved' ORDER BY created_at LIMIT 1");
        return new DeskStats(byStatus, open, oldest);
    }

    public async Task<NoteView> AddNoteAsync(string ticketId, string author, string text, CancellationToken ct)
    {
        RequireTicketId(ticketId);
        text = text?.Trim() ?? string.Empty;
        Require(text.Length is > 0 and <= 2_000, "The note must have 1 to 2,000 characters.");
        Require(!Redaction.ContainsCardNumber(text), "The note contains what looks like a card number. Never store card data in tickets; describe the issue instead.");
        Require(!Redaction.ContainsSecret(text), "The note contains what looks like a password, key or token. Remove it before saving.");
        await using var db = await database.OpenAsync(ct);
        await EnsureExistsAsync(db, ticketId);
        var now = clock.GetUtcNow();
        await db.ExecuteAsync(
            "INSERT INTO ticket_notes (ticket_id, author, text, created_at) VALUES (@ticketId, @author, @text, @now); UPDATE tickets SET updated_at = @now WHERE id = @ticketId",
            new { ticketId, author, text, now = now.ToString("O") });
        return new NoteView(author, text, now);
    }

    public async Task<TicketDetail> SetPriorityAsync(string ticketId, string priority, CancellationToken ct)
    {
        RequireTicketId(ticketId);
        Require(Priorities.Contains(priority), $"priority must be one of: {string.Join(", ", Priorities)}.");
        await using (var db = await database.OpenAsync(ct))
        {
            await EnsureExistsAsync(db, ticketId);
            await db.ExecuteAsync(
                "UPDATE tickets SET priority = @priority, updated_at = @now WHERE id = @ticketId",
                new { ticketId, priority, now = clock.GetUtcNow().ToString("O") });
        }

        return await GetAsync(ticketId, ct);
    }

    public async Task<TicketDetail> CloseAsync(string ticketId, string resolution, CancellationToken ct)
    {
        RequireTicketId(ticketId);
        resolution = resolution?.Trim() ?? string.Empty;
        Require(resolution.Length is >= 10 and <= 1_000, "Explain the resolution in 10 to 1,000 characters; the customer will read it.");
        Require(!Redaction.ContainsCardNumber(resolution) && !Redaction.ContainsSecret(resolution), "The resolution contains card data or a secret.");
        await using (var db = await database.OpenAsync(ct))
        {
            var status = await db.ExecuteScalarAsync<string?>("SELECT status FROM tickets WHERE id = @ticketId", new { ticketId })
                ?? throw new DeskRuleException($"Ticket {ticketId} does not exist.");
            Require(status != "solved", $"Ticket {ticketId} is already solved.");
            await db.ExecuteAsync(
                "UPDATE tickets SET status = 'solved', resolution = @resolution, updated_at = @now WHERE id = @ticketId",
                new { ticketId, resolution, now = clock.GetUtcNow().ToString("O") });
        }

        return await GetAsync(ticketId, ct);
    }

    private static async Task EnsureExistsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string ticketId)
    {
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tickets WHERE id = @ticketId", new { ticketId }) == 0)
        {
            throw new DeskRuleException($"Ticket {ticketId} does not exist. Use search_tickets to find ticket ids.");
        }
    }

    private static void RequireTicketId(string? ticketId) =>
        Require(TicketIdPattern().IsMatch(ticketId ?? string.Empty), "ticketId looks like T-1001.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new DeskRuleException(message);
        }
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^T-\d{4}$")]
    private static partial Regex TicketIdPattern();

    [GeneratedRegex(@"^C-\d{3}$")]
    private static partial Regex CustomerIdPattern();

    private sealed record SummaryRow(string Id, string Subject, string Status, string Priority, string Customer, string UpdatedAt);

    private sealed record TicketRow(
        string Id, string Subject, string Body, string Status, string Priority, string? Resolution, string CreatedAt, string UpdatedAt,
        string CustomerId, string Name, string Email, string Phone, string Plan);
}
