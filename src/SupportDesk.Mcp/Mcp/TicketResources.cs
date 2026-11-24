using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;
using SupportDesk.Mcp.Desk;
using SupportDesk.Mcp.Security;

namespace SupportDesk.Mcp.Mcp;

/// <summary>A ticket as a readable document, so a client can attach it to the conversation as context.</summary>
[McpServerResourceType]
[Authorize(Policy = Scopes.Read)]
public sealed class TicketResources(SupportDeskService desk)
{
    [McpServerResource(UriTemplate = "ticket://{ticketId}", Name = "ticket", Title = "Support ticket", MimeType = "text/markdown")]
    [Description("A support ticket with its customer (masked) and internal notes, as Markdown.")]
    public async Task<string> Ticket(string ticketId, CancellationToken cancellationToken)
    {
        var t = await desk.GetAsync(ticketId, cancellationToken);
        var md = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# {t.Id} · {t.Subject}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"**Status** {t.Status} · **Priority** {t.Priority} · **Customer** {t.Customer.Name} ({t.Customer.Plan}, {t.Customer.Email})")
            .AppendLine()
            .AppendLine(t.Body);
        foreach (var note in t.Notes)
        {
            md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"> **{note.Author}** ({note.CreatedAt:yyyy-MM-dd HH:mm}): {note.Text}");
        }

        return md.ToString();
    }
}

/// <summary>A reusable instruction for the model: how this desk wants tickets triaged.</summary>
[McpServerPromptType]
[Authorize(Policy = Scopes.Read)]
public sealed class TriagePrompts
{
    [McpServerPrompt(Name = "triage_ticket", Title = "Triage a ticket")]
    [Description("Step-by-step triage of one ticket using this server's tools.")]
    public static string TriageTicket([Description("Ticket id, e.g. T-1005.")] string ticketId) => $"""
        Triage support ticket {ticketId}.
        1. Call get_ticket and customer_history to understand the problem and whether it is recurring.
        2. Decide the priority: urgent if many users are blocked, high if one customer is blocked, normal or low otherwise.
           If it differs from the current one, call set_priority.
        3. Add an internal note with: summary in one sentence, likely cause, next step and owner team.
        4. Do not close the ticket. Do not include personal data, card numbers or credentials in notes.
        Answer with the decision and the note you added.
        """;
}
