using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;
using SupportDesk.Mcp.Desk;
using SupportDesk.Mcp.Security;

namespace SupportDesk.Mcp.Mcp;

/// <summary>
/// Read-only tools. The annotations tell the client these calls change nothing, so it can run them without asking.
/// Descriptions are written for the model: what the tool is for, and what the arguments look like.
/// </summary>
[McpServerToolType]
[Authorize(Policy = Scopes.Read)]
public sealed class TicketReadTools(SupportDeskService desk)
{
    [McpServerTool(Name = "search_tickets", Title = "Search tickets", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find support tickets by text, status or priority. Most urgent first. Use it before get_ticket when you do not know the ticket id.")]
    public Task<IReadOnlyList<TicketSummary>> SearchTickets(
        [Description("Words to look for in the subject or body, e.g. 'invoice' or 'SSO'.")] string? text = null,
        [Description("open, pending or solved.")] string? status = null,
        [Description("low, normal, high or urgent.")] string? priority = null,
        [Description("How many tickets to return, 1 to 25.")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        desk.SearchAsync(text, status, priority, limit, cancellationToken);

    [McpServerTool(Name = "get_ticket", Title = "Get a ticket", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("One ticket with its customer (email and phone masked) and its internal notes.")]
    public Task<TicketDetail> GetTicket(
        [Description("Ticket id, e.g. T-1001.")] string ticketId,
        CancellationToken cancellationToken = default) =>
        desk.GetAsync(ticketId, cancellationToken);

    [McpServerTool(Name = "customer_history", Title = "Customer history", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Every ticket of one customer, newest first. Useful to see whether a problem is recurring.")]
    public Task<IReadOnlyList<TicketSummary>> CustomerHistory(
        [Description("Customer id, e.g. C-001 (it is in get_ticket's result).")] string customerId,
        CancellationToken cancellationToken = default) =>
        desk.CustomerHistoryAsync(customerId, cancellationToken);

    [McpServerTool(Name = "ticket_stats", Title = "Desk statistics", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Ticket counts by status, open tickets by priority, and the oldest open ticket.")]
    public Task<DeskStats> TicketStats(CancellationToken cancellationToken = default) => desk.StatsAsync(cancellationToken);
}

/// <summary>
/// Tools that change data. Only clients with the tickets.write scope see them at all. Closing a ticket is marked
/// destructive, so well-behaved clients ask the user before calling it.
/// </summary>
[McpServerToolType]
[Authorize(Policy = Scopes.Write)]
public sealed class TicketWriteTools(SupportDeskService desk, IHttpContextAccessor http)
{
    [McpServerTool(Name = "add_internal_note", Title = "Add an internal note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Add a note visible only to the support team. Never include card numbers, passwords or keys: they are refused.")]
    public Task<NoteView> AddInternalNote(
        [Description("Ticket id, e.g. T-1001.")] string ticketId,
        [Description("The note, up to 2,000 characters.")] string note,
        CancellationToken cancellationToken = default) =>
        desk.AddNoteAsync(ticketId, Author(), note, cancellationToken);

    [McpServerTool(Name = "set_priority", Title = "Change the priority", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Set the priority of a ticket: low, normal, high or urgent.")]
    public Task<TicketDetail> SetPriority(
        [Description("Ticket id, e.g. T-1001.")] string ticketId,
        [Description("low, normal, high or urgent.")] string priority,
        CancellationToken cancellationToken = default) =>
        desk.SetPriorityAsync(ticketId, priority, cancellationToken);

    [McpServerTool(Name = "close_ticket", Title = "Close a ticket", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Mark a ticket as solved with a resolution the customer will read. Confirm with the user before calling it.")]
    public Task<TicketDetail> CloseTicket(
        [Description("Ticket id, e.g. T-1001.")] string ticketId,
        [Description("What was done, 10 to 1,000 characters, written for the customer.")] string resolution,
        CancellationToken cancellationToken = default) =>
        desk.CloseAsync(ticketId, resolution, cancellationToken);

    private string Author() => http.HttpContext?.User.Identity?.Name ?? "unknown-client";
}
