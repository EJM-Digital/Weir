using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The smaller parts starting the tray is made of: where the data folder is, the brand icon, the browser, the questions asked
/// before the tray exists, the errors nobody handles, and the real effects the start is wired to.
/// </summary>
public sealed class StartupPartsTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    private string Log => File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_data_folder_is_the_one_named_else_the_shared_program_data_folder_else_the_drive_default()
    {
        Assert.Equal(Path.Combine(_home.Path, "a"), Program.RuntimeHome(_ => $"  {_home.Path}\\a  ", () => "unused"));
        Assert.Equal(@"D:\Shared\Weir", Program.RuntimeHome(_ => " ", () => @"D:\Shared"));
        Assert.Equal(@"C:\ProgramData\Weir", Program.RuntimeHome(_ => null, () => ""));
    }

    [Fact]
    public void The_data_folder_without_a_variable_is_Weir_under_the_shared_program_data_folder()
    {
        var before = Environment.GetEnvironmentVariable(Program.RuntimeHomeVariable);
        Environment.SetEnvironmentVariable(Program.RuntimeHomeVariable, null);
        try
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Weir"), Program.RuntimeHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Program.RuntimeHomeVariable, before);
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("//elsewhere.example")]
    public void The_browser_is_only_opened_on_Weir_itself(string path)
    {
        var started = new List<ProcessStartInfo>();

        Assert.Throws<ArgumentException>(() => Program.OpenBrowser(8484, started.Add, path));

        Assert.Empty(started);
    }

    [Fact]
    public void The_brand_icon_comes_from_the_program_else_from_its_folder_else_from_the_system()
    {
        var elsewhere = typeof(string).Assembly;
        using var folder = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(folder.Path, "assets"));
        using (var inProgram = Program.LoadAppIcon(new Size(16, 16)))
        {
            Assert.Equal(16, inProgram.Width);
        }

        using (var fallback = Program.LoadAppIcon(null, elsewhere, folder.Path))
        {
            Assert.NotNull(fallback);
        }

        var resource = Assembly.GetAssembly(typeof(Program))!.GetManifestResourceNames().Single(n => n.EndsWith("weir-tray-icon.ico", StringComparison.OrdinalIgnoreCase));
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream(resource)!)
        using (var file = File.Create(Path.Combine(folder.Path, "assets", "weir-tray-icon.ico")))
        {
            stream.CopyTo(file);
        }
        using (var inAssets = Program.LoadAppIcon(null, elsewhere, folder.Path))
        {
            Assert.True(inAssets.Width > 0);
        }
        File.Move(Path.Combine(folder.Path, "assets", "weir-tray-icon.ico"), Path.Combine(folder.Path, "weir-tray-icon.ico"));
        using (var sized = Program.LoadAppIcon(new Size(16, 16), elsewhere, folder.Path))
        {
            Assert.Equal(16, sized.Width);
        }
    }

    [Fact]
    public void A_data_folder_another_account_owns_is_refused_and_left_alone()
    {
        var home = Path.Combine(_home.Path, "owned");
        Directory.CreateDirectory(home);

        var ex = Assert.Throws<RuntimeHomeOwnedByAnotherAccountException>(() => RuntimeHomeSecurity.Secure(home, _ => false));

        Assert.Contains(home, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_owner_with_no_name_on_this_PC_is_described_by_its_identifier()
    {
        var unknown = new SecurityIdentifier("S-1-5-21-1111-2222-3333-4444");

        Assert.Equal("S-1-5-21-1111-2222-3333-4444", RuntimeHomeSecurity.Describe(unknown));
        Assert.Contains("\\", RuntimeHomeSecurity.Describe(OwnerOnlyAccess.CurrentUser()), StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_nobody_handles_are_written_to_the_log_and_a_background_one_is_marked_as_seen()
    {
        UnhandledErrorLog.OnUiThread(new InvalidOperationException("a menu handler failed"));
        UnhandledErrorLog.OnFatal(new InvalidOperationException("the runtime is stopping"));
        var background = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("a task failed")));
        UnhandledErrorLog.OnUnobserved(background);

        Assert.Contains("Unhandled error on the tray's UI thread:", Log, StringComparison.Ordinal);
        Assert.Contains("a menu handler failed", Log, StringComparison.Ordinal);
        Assert.Contains("Unhandled error; the tray is stopping:", Log, StringComparison.Ordinal);
        Assert.Contains("a task failed", Log, StringComparison.Ordinal);
        Assert.True(background.Observed);
    }

    [Fact]
    public void The_port_question_keeps_an_icon_up_while_it_waits_and_returns_the_answer()
    {
        OnSta(() =>
        {
            var prompt = new PortPrompt(PortPromptReason.FirstRun, 8484, false, 8484);
            PortPrompt? asked = null;
            Icon? icon = null;

            var answer = StartupQuestions.AskForPort(prompt, (p, i) =>
            {
                asked = p;
                icon = i;
                return 9001;
            });

            Assert.Equal(9001, answer);
            Assert.Equal(prompt, asked);
            Assert.NotNull(icon);
            StartupQuestions.BringOpenDialogForward();
        });
    }

    [Fact]
    public void A_click_on_the_waiting_icon_brings_the_first_open_port_window_forward_and_does_nothing_without_one()
    {
        OnSta(() =>
        {
            var brought = new List<Form>();
            StartupQuestions.BringForward([], brought.Add);
            Assert.Empty(brought);

            using var first = new PortDialog(new PortPrompt(PortPromptReason.FirstRun, 8484, false, 8484), _ => false, null);
            using var second = new PortDialog(new PortPrompt(PortPromptReason.Change, 8484, false, 8484), _ => false, null);
            StartupQuestions.BringForward([first, second], brought.Add);

            Assert.Same(first, Assert.Single(brought));
        });
    }

    [Fact]
    public void The_installed_tray_is_the_program_the_hooks_run_from_and_updates_come_from_this_builds_channel()
    {
        Assert.Equal(Environment.ProcessPath, Program.InstalledTray());
        Assert.NotNull(UpdateChannel.SourceForThisBuild(ProjectLocation.RepositoryUrl));
    }

    [Fact]
    public void The_real_effects_are_wired_and_the_safe_ones_work_on_this_PC()
    {
        var real = StartupEffects.Windows;

        Assert.False(real.AnotherTrayRuns());
        Assert.NotNull(real.OpenFirewall().Rules);
        Assert.NotNull(real.OpenRunningMark());
        real.AtProcessExit(() => { });
        Assert.NotNull(StartupEffects.Windows.HasInteractiveDesktop);
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
