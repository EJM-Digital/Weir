using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The tray gives up the standard handles it inherited and points all three at the null device. The real handles belong to the test
/// host, so these tests drive <see cref="InheritedStdioHandles"/> through a fake of the calls it makes: what is closed, how many times,
/// what each standard handle ends up as, and what is logged when a call fails.
/// </summary>
public sealed class InheritedStdioHandlesTests
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private static readonly nint Nul = 900;

    private readonly Fake _calls = new();
    private readonly List<string> _log = [];

    private void Run() => InheritedStdioHandles.CloseInherited(_calls, _log.Add);

    [Fact]
    public void Three_separate_handles_are_each_closed_once_and_all_three_end_on_the_null_device()
    {
        _calls.Standard[StdInput] = 100;
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = 300;

        Run();

        Assert.Equal([100, 200, 300], _calls.Closed.Order());
        Assert.Equal([Nul, Nul, Nul], new[] { StdInput, StdOutput, StdError }.Select(id => _calls.Standard[id]));
        Assert.Empty(_log);
    }

    [Fact]
    public void Standard_output_and_error_sharing_one_handle_close_it_once_and_the_null_device_is_not_closed()
    {
        _calls.Standard[StdInput] = 100;
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = 200;

        Run();

        // Closing it twice would close whatever had been given that value in between: with the null device opened first, that is never it.
        Assert.Equal([100, 200], _calls.Closed.Order());
        Assert.DoesNotContain(Nul, _calls.Closed);
        Assert.Equal(Nul, _calls.Standard[StdOutput]);
        Assert.Equal(Nul, _calls.Standard[StdError]);
        Assert.Empty(_log);
    }

    [Fact]
    public void All_three_sharing_one_handle_close_it_once()
    {
        _calls.Standard[StdInput] = 700;
        _calls.Standard[StdOutput] = 700;
        _calls.Standard[StdError] = 700;

        Run();

        Assert.Equal([700], _calls.Closed);
    }

    [Fact]
    public void The_null_device_is_opened_before_anything_is_closed_so_that_its_handle_cannot_be_one_that_is_about_to_be_closed()
    {
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = 200;

        Run();

        Assert.True(_calls.OpenedBeforeFirstClose);
    }

    [Fact]
    public void Handles_that_are_zero_or_invalid_are_not_closed_but_still_pointed_at_the_null_device()
    {
        _calls.Standard[StdInput] = 0;
        _calls.Standard[StdOutput] = -1;
        _calls.Standard[StdError] = 0;

        Run();

        Assert.Empty(_calls.Closed);
        Assert.Equal([Nul, Nul, Nul], new[] { StdInput, StdOutput, StdError }.Select(id => _calls.Standard[id]));
    }

    [Fact]
    public void A_standard_handle_that_is_already_the_null_device_is_left_open()
    {
        _calls.Standard[StdInput] = Nul;
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = Nul;

        Run();

        Assert.Equal([200], _calls.Closed);
    }

    [Fact]
    public void A_handle_that_cannot_be_closed_is_logged_and_the_others_are_still_dealt_with()
    {
        _calls.Standard[StdInput] = 100;
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = 300;
        _calls.CloseFails.Add(200);

        Run();

        Assert.Equal([100, 200, 300], _calls.Closed.Order());
        Assert.Contains(_log, line => line.Contains("Could not close the inherited stdout handle (Win32 error 6)", StringComparison.Ordinal));
        Assert.Equal(Nul, _calls.Standard[StdError]);
    }

    [Fact]
    public void A_standard_handle_that_cannot_be_set_is_logged_with_which_one()
    {
        _calls.Standard[StdOutput] = 200;
        _calls.SetFails.Add(StdOutput);

        Run();

        Assert.Single(_log);
        Assert.Contains("Could not point the stdout handle at the null device (Win32 error 87)", _log[0], StringComparison.Ordinal);
        Assert.Equal(Nul, _calls.Standard[StdError]);
    }

    [Fact]
    public void When_the_null_device_cannot_be_opened_the_handles_are_closed_and_set_to_nothing_and_it_is_logged()
    {
        _calls.NullFails = true;
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = 200;

        Run();

        Assert.Equal([200], _calls.Closed);
        Assert.Equal(0, _calls.Standard[StdOutput]);
        Assert.Equal(0, _calls.Standard[StdError]);
        Assert.Contains(_log, line => line.Contains("Could not open the null device", StringComparison.Ordinal));
    }

    [Fact]
    public void Running_it_a_second_time_closes_nothing_more()
    {
        _calls.Standard[StdOutput] = 200;
        _calls.Standard[StdError] = 200;
        Run();
        _calls.Closed.Clear();

        Run();

        Assert.Empty(_calls.Closed);
    }

    private sealed class Fake : IStdHandleCalls
    {
        private bool _anyClosed;

        public Dictionary<int, nint> Standard { get; } = new() { [StdInput] = 0, [StdOutput] = 0, [StdError] = 0 };

        public List<nint> Closed { get; } = [];

        public HashSet<nint> CloseFails { get; } = [];

        public HashSet<int> SetFails { get; } = [];

        public bool NullFails { get; set; }

        public bool OpenedBeforeFirstClose { get; private set; } = true;

        public nint Get(int id) => Standard[id];

        public bool Close(nint handle, out int error)
        {
            _anyClosed = true;
            Closed.Add(handle);
            error = CloseFails.Contains(handle) ? 6 : 0;
            return error == 0;
        }

        public bool Set(int id, nint handle, out int error)
        {
            error = SetFails.Contains(id) ? 87 : 0;
            if (error == 0)
            {
                Standard[id] = handle;
            }

            return error == 0;
        }

        public nint OpenNullDevice()
        {
            if (_anyClosed)
            {
                OpenedBeforeFirstClose = false;
            }

            return NullFails ? throw new IOException("The system cannot find the file specified.") : Nul;
        }
    }
}
