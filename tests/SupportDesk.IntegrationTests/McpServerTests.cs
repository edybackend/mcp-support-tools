using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SupportDesk.IntegrationTests;

public sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The real server on a throw-away database; clients are the official MCP C# client over Streamable HTTP.</summary>
public sealed class SupportDeskServer : WebApplicationFactory<Program>
{
    public const string ReadOnlyKey = "dev-readonly-key-change-me";
    public const string AgentKey = "dev-agent-key-change-me";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"support-tests-{Guid.NewGuid():N}.db");

    public MovableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 24, 12, 0, 30, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:SupportDesk", $"Data Source={_db};Pooling=False");
        builder.UseSetting("ToolCalls:PerClientPerMinute", "30");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public async Task<McpClient> ConnectAsync(string? key)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(Server.BaseAddress, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = key is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        };
        var transport = new HttpClientTransport(options, CreateClient(), NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, null, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var file in new[] { _db, _db + "-wal", _db + "-shm" })
        {
            File.Delete(file);
        }
    }
}

public sealed class McpServerTests : IDisposable
{
    private readonly SupportDeskServer _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        client.CallToolAsync(tool, arguments, cancellationToken: Ct).AsTask();

    private static string Text(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task Without_a_key_the_connection_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => _server.ConnectAsync(key: null));

        Assert.Contains("401", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_key_sees_only_the_read_tools()
    {
        await using var reader = await _server.ConnectAsync(SupportDeskServer.ReadOnlyKey);
        await using var agent = await _server.ConnectAsync(SupportDeskServer.AgentKey);

        var readerTools = (await reader.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order();
        var agentTools = (await agent.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order();

        Assert.Equal(["customer_history", "get_ticket", "search_tickets", "ticket_stats"], readerTools);
        Assert.Equal(["add_internal_note", "close_ticket", "customer_history", "get_ticket", "search_tickets", "set_priority", "ticket_stats"], agentTools);
    }

    [Fact]
    public async Task Annotations_tell_the_client_which_tools_are_safe()
    {
        await using var agent = await _server.ConnectAsync(SupportDeskServer.AgentKey);
        var tools = (await agent.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name, t => t.ProtocolTool.Annotations!);

        Assert.True(tools["get_ticket"].ReadOnlyHint);
        Assert.True(tools["search_tickets"].IdempotentHint);
        Assert.False(tools["add_internal_note"].ReadOnlyHint);
        Assert.True(tools["close_ticket"].DestructiveHint);
        Assert.False(tools["set_priority"].DestructiveHint);
    }

    [Fact]
    public async Task A_read_only_key_cannot_call_a_write_tool()
    {
        await using var reader = await _server.ConnectAsync(SupportDeskServer.ReadOnlyKey);

        var refused = await Assert.ThrowsAsync<McpProtocolException>(() =>
            CallAsync(reader, "add_internal_note", new() { ["ticketId"] = "T-1001", ["note"] = "hello" }));

        Assert.Contains("forbidden", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Personal_data_is_masked_in_results()
    {
        await using var reader = await _server.ConnectAsync(SupportDeskServer.ReadOnlyKey);

        var result = await CallAsync(reader, "get_ticket", new() { ["ticketId"] = "T-1001" });
        var customer = result.StructuredContent!.Value.GetProperty("customer");

        Assert.Equal("a***@example.com", customer.GetProperty("email").GetString());
        Assert.Equal("***-4321", customer.GetProperty("phone").GetString());
        Assert.DoesNotContain("ana.souza@", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_returns_the_most_urgent_first()
    {
        await using var reader = await _server.ConnectAsync(SupportDeskServer.ReadOnlyKey);

        var result = await CallAsync(reader, "search_tickets", new() { ["status"] = "open", ["limit"] = 2 });

        Assert.Contains("T-1005", Text(result), StringComparison.Ordinal); // the only urgent one
        Assert.Contains("\"urgent\"", Text(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The customer read me the card 4242 4242 4242 4242", "card number")]
    [InlineData("Temporary access: password: hunter22", "password, key or token")]
    public async Task Notes_with_card_data_or_credentials_are_refused_and_not_stored(string note, string reason)
    {
        await using var agent = await _server.ConnectAsync(SupportDeskServer.AgentKey);

        var result = await CallAsync(agent, "add_internal_note", new() { ["ticketId"] = "T-1002", ["note"] = note });
        var ticket = await CallAsync(agent, "get_ticket", new() { ["ticketId"] = "T-1002" });

        Assert.True(result.IsError);
        Assert.Contains(reason, Text(result), StringComparison.Ordinal);
        Assert.Equal(0, ticket.StructuredContent!.Value.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task A_note_is_stored_with_the_client_as_author()
    {
        await using var agent = await _server.ConnectAsync(SupportDeskServer.AgentKey);

        await CallAsync(agent, "add_internal_note", new() { ["ticketId"] = "T-1002", ["note"] = "Checked the mail logs: the reset email bounced." });
        var notes = (await CallAsync(agent, "get_ticket", new() { ["ticketId"] = "T-1002" })).StructuredContent!.Value.GetProperty("notes");

        Assert.Equal("support-agent", notes[0].GetProperty("author").GetString());
    }

    [Fact]
    public async Task Rule_violations_come_back_as_errors_the_model_can_act_on()
    {
        await using var agent = await _server.ConnectAsync(SupportDeskServer.AgentKey);

        var badId = await CallAsync(agent, "get_ticket", new() { ["ticketId"] = "1001" });
        var unknown = await CallAsync(agent, "get_ticket", new() { ["ticketId"] = "T-9999" });
        var closed = await CallAsync(agent, "close_ticket", new() { ["ticketId"] = "T-1004", ["resolution"] = "Seats were added last week." });
        var badPriority = await CallAsync(agent, "set_priority", new() { ["ticketId"] = "T-1002", ["priority"] = "critical" });

        Assert.Contains("looks like T-1001", Text(badId), StringComparison.Ordinal);
        Assert.Contains("search_tickets", Text(unknown), StringComparison.Ordinal);
        Assert.Contains("already solved", Text(closed), StringComparison.Ordinal);
        Assert.Contains("low, normal, high, urgent", Text(badPriority), StringComparison.Ordinal);
        Assert.All([badId, unknown, closed, badPriority], r => Assert.True(r.IsError));
    }

    [Fact]
    public async Task The_31st_call_in_a_minute_is_refused_and_the_next_minute_works()
    {
        await using var reader = await _server.ConnectAsync(SupportDeskServer.ReadOnlyKey);
        for (var i = 0; i < 30; i++)
        {
            Assert.NotEqual(true, (await CallAsync(reader, "ticket_stats", [])).IsError);
        }

        var limited = await CallAsync(reader, "ticket_stats", []);
        Assert.True(limited.IsError);
        Assert.Contains("Rate limit", Text(limited), StringComparison.Ordinal);

        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotEqual(true, (await CallAsync(reader, "ticket_stats", [])).IsError);
    }

    [Fact]
    public async Task Every_call_is_audited_with_redacted_arguments()
    {
        await using var agent = await _server.ConnectAsync(SupportDeskServer.AgentKey);
        await CallAsync(agent, "add_internal_note", new() { ["ticketId"] = "T-1002", ["note"] = "card 4242 4242 4242 4242" });
        await CallAsync(agent, "get_ticket", new() { ["ticketId"] = "T-1002" });

        using var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", SupportDeskServer.AgentKey);
        var audit = JsonDocument.Parse(await http.GetStringAsync("/audit", Ct)).RootElement;

        Assert.Equal(["get_ticket ok", "add_internal_note error"], audit.EnumerateArray().Select(r => $"{r.GetProperty("tool")} {r.GetProperty("outcome")}"));
        Assert.Contains("[card]", audit[1].GetProperty("arguments").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("4242", audit.ToString(), StringComparison.Ordinal);

        http.DefaultRequestHeaders.Authorization = new("Bearer", SupportDeskServer.ReadOnlyKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/audit", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_ticket_resource_and_the_triage_prompt_are_available()
    {
        await using var reader = await _server.ConnectAsync(SupportDeskServer.ReadOnlyKey);

        var resource = await reader.ReadResourceAsync("ticket://T-1005", cancellationToken: Ct);
        var prompt = await reader.GetPromptAsync("triage_ticket", new Dictionary<string, object?> { ["ticketId"] = "T-1005" }, cancellationToken: Ct);

        var markdown = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text;
        Assert.StartsWith("# T-1005 · Dashboard is blank", markdown, StringComparison.Ordinal);
        Assert.Contains("d***@example.com", markdown, StringComparison.Ordinal);
        Assert.Contains("Do not close the ticket", JsonSerializer.Serialize(prompt.Messages), StringComparison.Ordinal);
    }

    public void Dispose() => _server.Dispose();
}
