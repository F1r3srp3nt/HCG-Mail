using HCG.Mail.Core.Addresses;

namespace HCG.Mail.Tests.Core;

public class EmailAddressTests
{
    [Theory]
    [InlineData("ola@firma.no")]
    [InlineData("ola.nordmann+tag@sub.firma.no")]
    [InlineData("o'brien@example.co.uk")]
    [InlineData("kari@blåbær.no")]
    [InlineData("æøå@example.no")]
    public void Valid_addresses(string address) => Assert.True(EmailAddressValidator.IsValid(address));

    [Theory]
    [InlineData("")]
    [InlineData("ola")]
    [InlineData("ola@")]
    [InlineData("@firma.no")]
    [InlineData("ola@@firma.no")]
    [InlineData("ola@firma")]
    [InlineData("ola nordmann@firma.no")]
    [InlineData(".ola@firma.no")]
    [InlineData("ola..n@firma.no")]
    [InlineData("ola@firma..no")]
    [InlineData("ola@192.0.2.1")]
    [InlineData("ola@[192.0.2.1]")]
    [InlineData("ola@firma.n")]
    [InlineData("ola<x>@firma.no")]
    public void Invalid_addresses(string address) => Assert.False(EmailAddressValidator.IsValid(address));

    [Fact]
    public void Parses_mixed_recipient_list()
    {
        var result = RecipientParser.Parse("Ola Nordmann <ola@firma.no>, \"Doe, Jane\" <jane@example.com>; kari@firma.no");
        Assert.True(result.IsValid);
        Assert.Equal(3, result.Addresses.Count);
        Assert.Equal(new EmailAddress("Ola Nordmann", "ola@firma.no"), result.Addresses[0]);
        Assert.Equal(new EmailAddress("Doe, Jane", "jane@example.com"), result.Addresses[1]);
        Assert.Equal(new EmailAddress(null, "kari@firma.no"), result.Addresses[2]);
    }

    [Fact]
    public void Reports_invalid_tokens_and_removes_duplicates()
    {
        var result = RecipientParser.Parse("ola@firma.no, not-an-address, OLA@FIRMA.NO, ,");
        Assert.Single(result.Addresses);
        Assert.Equal(["not-an-address"], result.Invalid);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_input_is_valid_and_empty()
    {
        var result = RecipientParser.Parse("   ");
        Assert.Empty(result.Addresses);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_trailing_garbage_after_angle_address()
    {
        Assert.False(RecipientParser.TryParseSingle("Ola <ola@firma.no> extra", out _));
    }

    [Fact]
    public void Formatting_quotes_names_with_special_characters_and_round_trips()
    {
        var addresses = new[] { new EmailAddress("Doe, Jane", "jane@example.com"), new EmailAddress(null, "kari@firma.no") };
        var text = RecipientParser.Format(addresses);
        Assert.Equal("\"Doe, Jane\" <jane@example.com>, kari@firma.no", text);
        Assert.Equal(addresses, RecipientParser.Parse(text).Addresses);
    }

    [Fact]
    public void Same_mailbox_comparison_is_case_insensitive()
    {
        Assert.True(new EmailAddress("A", "Ola@Firma.no").IsSameMailbox("ola@firma.NO"));
    }
}

public class MailtoParserTests
{
    [Fact]
    public void Parses_address_subject_and_body()
    {
        var link = MailtoParser.TryParse("mailto:ola@firma.no?subject=Hei%20p%C3%A5%20deg&body=Line%201%0ALine%202");
        Assert.NotNull(link);
        Assert.Equal("ola@firma.no", Assert.Single(link!.To).Address);
        Assert.Equal("Hei på deg", link.Subject);
        Assert.Equal("Line 1\nLine 2", link.Body);
    }

    [Fact]
    public void Supports_multiple_recipients_and_ignores_hidden_ones()
    {
        var link = MailtoParser.TryParse("mailto:a@example.com,b@example.com?to=c@example.com&cc=x@evil.example&bcc=y@evil.example")!;
        Assert.Equal(["a@example.com", "b@example.com", "c@example.com"], link.To.Select(t => t.Address));
    }

    [Fact]
    public void Drops_invalid_addresses_and_header_injection()
    {
        var link = MailtoParser.TryParse("mailto:not-an-address?subject=a%0D%0ABcc:%20x@evil.example")!;
        Assert.Empty(link.To);
        Assert.DoesNotContain('\n', link.Subject);
        Assert.DoesNotContain('\r', link.Subject);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_mailto_links_are_not_parsed(string? link) => Assert.Null(MailtoParser.TryParse(link));
}
