using Dapper;
using Microsoft.Data.Sqlite;

namespace SupportDesk.Mcp.Desk;

/// <summary>The support desk's SQLite database: customers, tickets, internal notes and the audit log of tool calls.</summary>
public sealed class SupportDatabase(string connectionString)
{
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(TimeProvider clock, bool seed)
    {
        var file = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await using var db = await OpenAsync();
        await db.ExecuteAsync("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS customers (
                id    TEXT PRIMARY KEY,
                name  TEXT NOT NULL,
                email TEXT NOT NULL,
                phone TEXT NOT NULL,
                plan  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tickets (
                id          TEXT PRIMARY KEY,
                customer_id TEXT NOT NULL REFERENCES customers (id),
                subject     TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL,
                priority    TEXT NOT NULL,
                resolution  TEXT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ticket_notes (
                ticket_id  TEXT NOT NULL REFERENCES tickets (id),
                author     TEXT NOT NULL,
                text       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                at          TEXT NOT NULL,
                client      TEXT NOT NULL,
                tool        TEXT NOT NULL,
                arguments   TEXT NOT NULL,   -- redacted
                outcome     TEXT NOT NULL,   -- ok | error | denied | rate_limited
                detail      TEXT NULL,
                duration_ms INTEGER NOT NULL
            );
            """);

        if (!seed || await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM customers") > 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        string Ago(double hours) => (now - TimeSpan.FromHours(hours)).ToString("O");
        await using var tx = db.BeginTransaction();
        await db.ExecuteAsync(
            "INSERT INTO customers (id, name, email, phone, plan) VALUES (@Id, @Name, @Email, @Phone, @Plan)",
            new[]
            {
                new { Id = "C-001", Name = "Ana Souza", Email = "ana.souza@example.com", Phone = "+55 11 98765-4321", Plan = "Business" },
                new { Id = "C-002", Name = "Bruno Lima", Email = "bruno.lima@example.com", Phone = "+55 21 99876-1234", Plan = "Starter" },
                new { Id = "C-003", Name = "Carla Mendes", Email = "carla@example.org", Phone = "+1 415 555 0134", Plan = "Enterprise" },
                new { Id = "C-004", Name = "Diego Rocha", Email = "diego.rocha@example.com", Phone = "+55 31 97654-3210", Plan = "Business" },
            },
            tx);
        await db.ExecuteAsync(
            "INSERT INTO tickets (id, customer_id, subject, body, status, priority, created_at, updated_at) VALUES (@Id, @Customer, @Subject, @Body, @Status, @Priority, @Created, @Created)",
            new[]
            {
                new { Id = "T-1001", Customer = "C-001", Subject = "Invoice charged twice in November", Body = "We were charged twice for the November invoice. Please refund the duplicate.", Status = "open", Priority = "high", Created = Ago(30) },
                new { Id = "T-1002", Customer = "C-002", Subject = "Cannot reset my password", Body = "The reset email never arrives. I checked the spam folder.", Status = "open", Priority = "normal", Created = Ago(20) },
                new { Id = "T-1003", Customer = "C-003", Subject = "API returns 429 during our nightly import", Body = "Our nightly import of 50k records hits 429 after a few minutes. Can the limit be raised?", Status = "pending", Priority = "high", Created = Ago(52) },
                new { Id = "T-1004", Customer = "C-001", Subject = "Add two seats to our plan", Body = "We hired two people and need two more seats.", Status = "solved", Priority = "low", Created = Ago(200) },
                new { Id = "T-1005", Customer = "C-004", Subject = "Dashboard is blank since this morning", Body = "Since 8am the dashboard shows nothing for every user in our company.", Status = "open", Priority = "urgent", Created = Ago(3) },
                new { Id = "T-1006", Customer = "C-003", Subject = "SSO login loops back to the sign-in page", Body = "After the SAML login we land on the sign-in page again.", Status = "open", Priority = "high", Created = Ago(8) },
                new { Id = "T-1007", Customer = "C-002", Subject = "How do I export my data?", Body = "I want a CSV of all my projects.", Status = "pending", Priority = "low", Created = Ago(70) },
                new { Id = "T-1008", Customer = "C-004", Subject = "Wrong VAT number on invoices", Body = "Our VAT number changed in October; invoices still show the old one.", Status = "open", Priority = "normal", Created = Ago(26) },
            },
            tx);
        await db.ExecuteAsync(
            "INSERT INTO ticket_notes (ticket_id, author, text, created_at) VALUES (@Ticket, @Author, @Text, @Created)",
            new[]
            {
                new { Ticket = "T-1001", Author = "billing-team", Text = "Confirmed two charges with the same invoice id. Refund requires approval.", Created = Ago(28) },
                new { Ticket = "T-1003", Author = "api-team", Text = "Customer is on 600 req/min. Enterprise can go to 2,000 with a contract change.", Created = Ago(50) },
            },
            tx);
        tx.Commit();
    }
}
