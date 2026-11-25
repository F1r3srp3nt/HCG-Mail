using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using HCG.Mail.Core.Settings;
using HCG.Mail.Mobile.Services;
using HCG.Mail.Mobile.Views;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HCG.Mail.Mobile.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StartupTraceCollection
{
    public const string Name = "start-up trace (process-wide state)";
}

/// <summary>The start-up trace survives a crash, names the last stage, keeps the previous launch and holds no secrets.</summary>
[Collection(StartupTraceCollection.Name)]
public sealed class StartupTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hcg-startup-" + Guid.NewGuid().ToString("N"));

    public StartupTraceTests() => StartupTrace.ResetForTests();

    public void Dispose()
    {
        StartupTrace.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string[] Lines() => File.ReadAllLines(Path.Combine(_dir, StartupTrace.FileName));

    [Fact]
    public void Each_stage_is_on_disk_as_soon_as_it_is_marked()
    {
        Assert.Equal(Path.Combine(_dir, StartupTrace.FileName), StartupTrace.Begin([_dir], "HCG Mail 0.1.0 (2) · test"));
        StartupTrace.Mark(StartupStage.NativeEntry, "process started");
        StartupTrace.Mark(StartupStage.RuntimeReady);

        var lines = Lines(); // read by another handle while the "app" still runs, as after a crash
        Assert.StartsWith("HCG Mail start-up trace · HCG Mail 0.1.0 (2) · test", lines[0]);
        Assert.Contains(lines, l => l.Contains(" ms  native-entry · process started"));
        Assert.EndsWith(" ms  runtime-ready", lines[^1]);
        Assert.Equal(StartupStage.RuntimeReady, StartupTrace.LastStage);
    }

    [Fact]
    public void A_launch_that_stopped_before_its_first_frame_is_reported_on_the_next_launch()
    {
        StartupTrace.Begin([_dir], "first launch");
        StartupTrace.Mark(StartupStage.AvaloniaInit);
        StartupTrace.Mark(StartupStage.Settings);
        // ... the process is killed here.
        StartupTrace.ResetForTests();

        StartupTrace.Begin([_dir], "second launch");
        Assert.Equal("settings", StartupTrace.PreviousLaunchStoppedAt);
        Assert.Contains(File.ReadAllLines(Path.Combine(_dir, StartupTrace.PreviousFileName)), l => l.EndsWith(" ms  settings"));
        Assert.Single(Lines()); // the new trace starts empty apart from its header

        StartupTrace.Mark(StartupStage.FirstFrame);
        StartupTrace.ResetForTests();
        StartupTrace.Begin([_dir], "third launch");
        Assert.Null(StartupTrace.PreviousLaunchStoppedAt);
    }

    [Fact]
    public void An_unwritable_folder_falls_back_to_the_next_one()
    {
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "a file, not a folder");
        var fallback = Path.Combine(_dir, "fallback");

        Assert.Equal(Path.Combine(fallback, StartupTrace.FileName), StartupTrace.Begin([Path.Combine(blocked, "startup"), fallback], "test"));
    }

    [Fact]
    public void Nothing_written_is_ever_a_credential()
    {
        StartupTrace.Begin([_dir], "user alice@example.com password=hunter2");
        try
        {
            throw new InvalidOperationException("AUTHENTICATE PLAIN AGFsaWNlAGh1bnRlcjI= rejected for bob@example.org, access_token=abc.def.ghi");
        }
        catch (Exception ex)
        {
            StartupTrace.Failure("signing in", ex);
        }

        var text = File.ReadAllText(Path.Combine(_dir, StartupTrace.FileName));
        Assert.DoesNotContain("alice@", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("bob@", text);
        Assert.DoesNotContain("AGFsaWNlAGh1bnRlcjI=", text);
        Assert.DoesNotContain("abc.def.ghi", text);
        Assert.Contains("FAILED in signing in", text);
        Assert.Contains("System.InvalidOperationException (0x80131509)", text);
        Assert.Contains(nameof(Nothing_written_is_ever_a_credential), text); // the stack trace is kept
    }

    [Fact]
    public void Without_a_writable_folder_marking_still_works()
    {
        Assert.Null(StartupTrace.Begin([], "no folders"));
        StartupTrace.Mark(StartupStage.MainView);
        StartupTrace.Failure("anything", new IOException("disk full"));
        Assert.Equal(StartupStage.MainView, StartupTrace.LastStage);
    }

    [AvaloniaFact]
    public async Task The_app_records_every_shared_stage_through_the_first_frame_and_deferred_start_up()
    {
        StartupTrace.Begin([_dir], "headless");
        var firstFrame = false;
        StartupTrace.FirstFrameRendered += () => firstFrame = true;

        await using (var ui = await MobileHarness.StartAsync(Devices.IPhone, realMode: true))
            await MobileHarness.WaitUntil(() => Lines().Any(l => l.Contains(" ms  deferred-services")) && Lines().Any(l => l.Contains(" ms  first-frame")),
                "first frame and deferred start-up", detail: () => string.Join(" | ", Lines()));

        var stages = Lines().Skip(1).Select(l => l[(l.IndexOf(" ms  ", StringComparison.Ordinal) + 5)..].Split(' ')[0]).ToList();
        foreach (var stage in new[] { "local-storage", "settings", "main-view", "main-view-attached", "first-frame", "deferred-services" })
            Assert.Contains(stage, stages);
        Assert.True(stages.IndexOf("main-view") < stages.IndexOf("main-view-attached"));
        Assert.True(stages.IndexOf("main-view-attached") < stages.IndexOf("first-frame"));
        Assert.True(firstFrame);
        Assert.DoesNotContain(stages, s => s == "FAILED");
    }

    [AvaloniaFact]
    public async Task A_mailbox_start_up_failure_is_reported_and_the_app_keeps_running()
    {
        StartupTrace.Begin([_dir], "headless");
        var settings = Substitute.For<IAppSettingsStore>();
        settings.Load().Returns(new AppSettings());
        var accounts = Substitute.For<IAccountStore>();
        accounts.Load().Returns(_ => throw new IOException("accounts.json is unreadable"));

        await using var ui = await MobileHarness.StartAsync(Devices.IPhone, realMode: true, configure: s =>
        {
            s.AddSingleton(settings);
            s.AddSingleton(accounts);
        });
        await MobileHarness.WaitUntil(() => File.ReadAllText(Path.Combine(_dir, StartupTrace.FileName)).Contains("FAILED in starting the mailbox"), "the failure in the trace");

        Assert.True(ui.View.IsVisible);
        Assert.Contains("System.IO.IOException", File.ReadAllText(Path.Combine(_dir, StartupTrace.FileName)));
    }

    [AvaloniaFact]
    public void The_failure_screen_names_the_stage_and_offers_the_report()
    {
        StartupTrace.Begin([_dir], "headless");
        StartupTrace.Mark(StartupStage.Settings);
        var platform = new FakeMobilePlatform();

        var view = StartupFailureView.Create(new InvalidOperationException("secret detail"), platform);
        var window = new Avalonia.Controls.Window { Content = view, Width = 390, Height = 844 };
        window.Show();
        var texts = view.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? "").ToList();
        var buttons = view.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().ToList();
        window.Close();

        Assert.Contains(texts, t => t.Contains("\"settings\"") && t.Contains(nameof(InvalidOperationException)));
        Assert.DoesNotContain(texts, t => t.Contains("secret detail")); // the type only, never the message
        Assert.Single(buttons);
    }
}
