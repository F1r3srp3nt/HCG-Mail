using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Core;

public class SignatureTests
{
    private const string Sig = "Kari Nordmann\nHakmi Capital Group";

    [Fact]
    public void New_messages_end_with_the_signature_after_the_standard_separator()
    {
        Assert.Equal("\n\n-- \nKari Nordmann\nHakmi Capital Group", SignatureBlock.Insert("", Sig, ComposeKind.New));
    }

    [Theory]
    [InlineData(ComposeKind.Reply)]
    [InlineData(ComposeKind.Forward)]
    public void Replies_and_forwards_get_it_above_the_quoted_text(ComposeKind kind)
    {
        var body = SignatureBlock.Insert("\n\nOn Monday Ola wrote:\n> Hello", Sig, kind);
        Assert.StartsWith("\n\n-- \nKari Nordmann", body);
        Assert.True(body.IndexOf("-- \n", StringComparison.Ordinal) < body.IndexOf("Ola wrote", StringComparison.Ordinal));
    }

    [Fact]
    public void Drafts_and_accounts_without_a_signature_are_left_alone()
    {
        Assert.Equal("Draft text", SignatureBlock.Insert("Draft text", Sig, ComposeKind.EditDraft));
        Assert.Equal("", SignatureBlock.Insert("", null, ComposeKind.New));
    }

    [Fact]
    public void Changing_account_swaps_an_unchanged_signature_but_never_an_edited_one()
    {
        var body = "Hello\n\n" + SignatureBlock.Block(Sig);
        Assert.EndsWith("-- \nOla", SignatureBlock.Replace(body, Sig, "Ola"));
        Assert.Equal("Hello\n\n", SignatureBlock.Replace(body, Sig, null));
        var edited = body.Replace("Hakmi Capital Group", "HCG");
        Assert.Equal(edited, SignatureBlock.Replace(edited, Sig, "Ola"));
    }

    [Fact]
    public void Signatures_are_saved_per_account_and_survive_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-signatures-").FullName;
        try
        {
            var paths = new AppPaths(dir);
            var settings = new AppSettings().WithSignature("a1", Sig).WithSignature("a2", "Ola").WithSignature("a2", "Ola N.");
            new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance).Save(settings);
            var loaded = new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance).Load();
            Assert.Equal(Sig, loaded.SignatureFor("a1"));
            Assert.Equal("Ola N.", loaded.SignatureFor("a2"));
            Assert.Null(loaded.WithSignature("a1", " ").SignatureFor("a1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
