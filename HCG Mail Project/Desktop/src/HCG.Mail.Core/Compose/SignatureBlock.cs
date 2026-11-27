namespace HCG.Mail.Core.Compose;

/// <summary>
/// Places an account's signature in a message body, after the standard "-- " separator line (RFC 3676), so that mail
/// programs recognize it. New messages: after the (empty) body. Replies and forwards: above the quoted original, where
/// the reply is written. Drafts already contain whatever the user kept, so nothing is added to them.
/// </summary>
public static class SignatureBlock
{
    public const string Separator = "-- \n";

    public static string Block(string signature) => Separator + signature.Replace("\r\n", "\n").TrimEnd();

    public static string Insert(string body, string? signature, ComposeKind kind)
    {
        if (string.IsNullOrWhiteSpace(signature) || kind == ComposeKind.EditDraft)
            return body;
        var block = Block(signature);
        if (kind == ComposeKind.New)
            return string.IsNullOrEmpty(body) ? "\n\n" + block : body.TrimEnd() + "\n\n" + block;
        // Reply/forward bodies start with blank lines for the user's text, then the quote.
        var quote = body.TrimStart('\n');
        return "\n\n" + block + "\n\n" + quote;
    }

    /// <summary>
    /// Swaps the signature when the sending account changes, but only if the old signature is still there exactly as
    /// inserted: a signature the user edited is left alone.
    /// </summary>
    public static string Replace(string body, string? oldSignature, string? newSignature)
    {
        if (string.IsNullOrWhiteSpace(oldSignature))
            return body;
        var oldBlock = Block(oldSignature);
        var index = body.IndexOf(oldBlock, StringComparison.Ordinal);
        if (index < 0 || body.IndexOf(oldBlock, index + 1, StringComparison.Ordinal) >= 0)
            return body;
        return body[..index] + (string.IsNullOrWhiteSpace(newSignature) ? "" : Block(newSignature)) + body[(index + oldBlock.Length)..];
    }
}
