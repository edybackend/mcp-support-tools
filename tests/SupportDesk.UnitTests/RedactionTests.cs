using SupportDesk.Mcp.Security;

namespace SupportDesk.UnitTests;

public class RedactionTests
{
    [Theory]
    [InlineData("ana.souza@example.com", "a***@example.com")]
    [InlineData("x@y.io", "x***@y.io")]
    [InlineData("not-an-email", "***")]
    public void Emails_keep_only_the_first_letter_and_the_domain(string email, string masked) =>
        Assert.Equal(masked, Redaction.MaskEmail(email));

    [Theory]
    [InlineData("+55 11 98765-4321", "***-4321")]
    [InlineData("12", "***")]
    public void Phones_keep_only_the_last_four_digits(string phone, string masked) =>
        Assert.Equal(masked, Redaction.MaskPhone(phone));

    [Theory]
    [InlineData("card 4242 4242 4242 4242 please", true)]
    [InlineData("card 4242-4242-4242-4242", true)]
    [InlineData("amex 378282246310005", true)]
    [InlineData("order 4242 4242 4242 4241", false)] // fails the Luhn check
    [InlineData("invoice 2026-11-24 for 1,299.00", false)]
    [InlineData("call +55 11 98765-4321", false)]
    public void Card_numbers_are_found_by_shape_and_Luhn(string text, bool found) =>
        Assert.Equal(found, Redaction.ContainsCardNumber(text));

    [Theory]
    [InlineData("password: hunter22", true)]
    [InlineData("api_key=abc123", true)]
    [InlineData("use sk-ABCDEFGHIJKLMNOPQRSTUV to call", true)]
    [InlineData("the customer forgot the password", false)]
    public void Credentials_are_recognised(string text, bool found) =>
        Assert.Equal(found, Redaction.ContainsSecret(text));

    [Fact]
    public void Audit_text_hides_cards_and_secrets_and_is_cut()
    {
        Assert.Equal("""{"note":"card [card] and [secret]"}""", Redaction.ForAudit("""{"note":"card 4242 4242 4242 4242 and token=abc"}"""));
        Assert.Equal(201, Redaction.ForAudit(new string('x', 500)).Length);
    }
}
