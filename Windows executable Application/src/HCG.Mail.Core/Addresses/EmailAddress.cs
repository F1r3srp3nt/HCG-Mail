namespace HCG.Mail.Core.Addresses;

public sealed record EmailAddress(string? Name, string Address)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name!;

    public bool IsSameMailbox(EmailAddress other) => IsSameMailbox(other.Address);

    public bool IsSameMailbox(string address) =>
        string.Equals(Address.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Formats the address for an editable recipient field, quoting the name when needed.</summary>
    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return Address;
        var needsQuotes = Name!.IndexOfAny([',', ';', '<', '>', '"', '@', '(', ')', ':', '[', ']', '\\']) >= 0;
        var name = needsQuotes ? "\"" + Name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : Name;
        return $"{name} <{Address}>";
    }
}
