using HCG.Mail.Core.Mail;

namespace HCG.Mail.Tests.Core;

public class AttachmentFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("C:\\Users\\x\\file.txt", "file.txt")]
    [InlineData("/absolute/path/file.txt", "file.txt")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("a<b>c:d\"e|f*g.txt", "a_b_c_d_e_f_g.txt")]
    [InlineData("invoice.pdf.", "invoice.pdf")]
    [InlineData("invoice.pdf   ", "invoice.pdf")]
    [InlineData(".hidden", "hidden")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData("", "attachment")]
    [InlineData("   ", "attachment")]
    [InlineData(null, "attachment")]
    [InlineData("...", "attachment")]
    [InlineData("ok\u0000name.txt", "okname.txt")]
    public void Sanitizes_names(string? input, string expected) =>
        Assert.Equal(expected, AttachmentFileNames.Sanitize(input));

    [Fact]
    public void Removes_bidi_override_used_to_disguise_extensions()
    {
        // "invoice\u202Efdp.exe" displays as "invoiceexe.pdf" in many UIs.
        var sanitized = AttachmentFileNames.Sanitize("invoice\u202Efdp.exe");
        Assert.Equal("invoicefdp.exe", sanitized);
        Assert.True(AttachmentFileNames.IsPotentiallyDangerous("invoice\u202Efdp.exe"));
    }

    [Fact]
    public void Truncates_long_names_but_keeps_extension()
    {
        var sanitized = AttachmentFileNames.Sanitize(new string('a', 400) + ".docx");
        Assert.True(sanitized.Length <= 150);
        Assert.EndsWith(".docx", sanitized);
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("script.JS", true)]
    [InlineData("macro.docm", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("report.pdf", false)]
    [InlineData("data.csv", false)]
    public void Flags_dangerous_extensions(string name, bool dangerous) =>
        Assert.Equal(dangerous, AttachmentFileNames.IsPotentiallyDangerous(name));

    [Fact]
    public void Unique_path_appends_counter_and_stays_inside_directory()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-test-").FullName;
        try
        {
            var first = AttachmentFileNames.GetUniquePath(dir, "report.pdf");
            Assert.Equal(Path.Combine(dir, "report.pdf"), first);
            File.WriteAllText(first, "x");
            var second = AttachmentFileNames.GetUniquePath(dir, "report.pdf");
            Assert.Equal(Path.Combine(dir, "report (2).pdf"), second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Path_outside_download_directory_is_refused()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-test-").FullName;
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => AttachmentFileNames.GetUniquePath(dir, "../escape.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
