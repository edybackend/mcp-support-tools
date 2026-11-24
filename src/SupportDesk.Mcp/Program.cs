using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using SupportDesk.Mcp.Desk;
using SupportDesk.Mcp.Mcp;
using SupportDesk.Mcp.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SupportDatabase(builder.Configuration.GetConnectionString("SupportDesk")
    ?? throw new InvalidOperationException("Connection string 'SupportDesk' is missing.")));
builder.Services.AddSingleton<SupportDeskService>();
builder.Services.AddSingleton<ToolCallGuard>();
builder.Services.Configure<ToolCallOptions>(builder.Configuration.GetSection(ToolCallOptions.SectionName));
builder.Services.Configure<ApiClientsOptions>(o => builder.Configuration.GetSection(ApiClientsOptions.SectionName).Bind(o.Clients));
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Scopes.Read, p => p.RequireClaim("scope", Scopes.Read))
    .AddPolicy(Scopes.Write, p => p.RequireClaim("scope", Scopes.Write))
    .AddPolicy(Scopes.Audit, p => p.RequireClaim("scope", Scopes.Audit));

builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "support-desk", Title = "Support desk tools", Version = "1.0.0" };
        options.ServerInstructions =
            "Tools for a customer support desk. Read tools are safe to call. Write tools need the tickets.write scope; "
            + "confirm with the user before close_ticket. Personal data is masked; never ask for or store card data or credentials.";
    })
    .WithHttpTransport(http => http.Stateless = true) // no session state: any instance behind a load balancer can answer
    .AddAuthorizationFilters()                          // [Authorize] on tools: hidden from tools/list and refused on call
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, ct) =>
        context.Services!.GetRequiredService<ToolCallGuard>().InvokeAsync(context, next, ct)))
    .WithTools<TicketReadTools>()
    .WithTools<TicketWriteTools>()
    .WithResources<TicketResources>()
    .WithPrompts<TriagePrompts>();

builder.Services.AddHealthChecks();

var app = builder.Build();

await app.Services.GetRequiredService<SupportDatabase>().InitializeAsync(TimeProvider.System, app.Configuration.GetValue("SupportDesk:SeedSampleData", false));

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapMcp("/mcp").RequireAuthorization(Scopes.Read);
app.MapGet("/audit", async (SupportDatabase database, CancellationToken ct) =>
    {
        await using var db = await database.OpenAsync(ct);
        return Results.Ok(await Dapper.SqlMapper.QueryAsync(db, "SELECT at, client, tool, arguments, outcome, detail, duration_ms AS durationMs FROM audit_log ORDER BY id DESC LIMIT 100"));
    })
    .RequireAuthorization(Scopes.Audit);
app.MapGet("/", () => Results.Text("Support desk MCP server. Endpoint: POST /mcp (Streamable HTTP) with Authorization: Bearer <key>. Audit: GET /audit."));

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
