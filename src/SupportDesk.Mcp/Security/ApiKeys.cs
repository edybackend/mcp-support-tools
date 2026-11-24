using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SupportDesk.Mcp.Security;

public static class Scopes
{
    public const string Read = "tickets.read";
    public const string Write = "tickets.write";
    public const string Audit = "audit.read";
}

/// <summary>One AI client allowed to connect. Only the SHA-256 of its key is configured, never the key itself.</summary>
public sealed class ApiClient
{
    public string Name { get; set; } = string.Empty;

    public string KeySha256 { get; set; } = string.Empty;

    public List<string> Scopes { get; set; } = [];
}

public sealed class ApiClientsOptions
{
    public const string SectionName = "Clients";

    public List<ApiClient> Clients { get; set; } = [];
}

/// <summary>
/// Authenticates <c>Authorization: Bearer &lt;key&gt;</c> (or <c>X-Api-Key</c>). The principal carries the client name and
/// one <c>scope</c> claim per granted scope; tools are then authorized with ordinary ASP.NET Core policies.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiClientsOptions> clients) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var hash = Convert.FromHexString(Hex(key));
        var client = clients.Value.Clients.FirstOrDefault(c =>
            c.KeySha256.Length == 64 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(c.KeySha256), hash));
        if (client is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, client.Name), .. client.Scopes.Select(s => new Claim("scope", s))],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    public static string Hex(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
