using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Weir.Tray;

/// <summary>
/// The one window the tray owns: asks which port Weir's web app should live at. Shown on an
/// interactive first run, when the saved port is taken on a later start, and from the tray
/// menu's "Change port". Never shown without a person at a desktop (see
/// <see cref="PortChoice.HasInteractiveDesktop"/>).
///
/// Plain WinForms in the system dialog font, like the rest of the tray (a NotifyIcon, its
/// context menu and balloon tips): no custom chrome, so it looks like Windows.
/// </summary>
sealed class PortDialog : Form
{
    private readonly PortPrompt _prompt;
    private readonly Func<int, bool> _isInUse;
    private readonly RadioButton _useDefault;
    private readonly RadioButton _useCustom;
    private readonly TextBox _customPort;
    private readonly Label _error;
    private readonly Label _address;

    /// <summary>The port chosen, once the dialog closes with OK.</summary>
    internal int? ChosenPort { get; private set; }

    // For the tests: the two options and the text box, as a person would use them.
    internal RadioButton DefaultOption => _useDefault;
    internal RadioButton CustomOption => _useCustom;
    internal TextBox CustomPortBox => _customPort;
    internal string AddressText => _address.Text;
    internal string? ErrorText => string.IsNullOrEmpty(_error.Text) ? null : _error.Text;
    internal void PressOk() => Accept();

    /// <summary>The question comes from start-up, not from the menu: it stays in front, with a taskbar button, until it is answered.</summary>
    internal bool MustBeSeen => _prompt.Reason != PortPromptReason.Change;

    private const int TopMostStyle = 0x00000008;

    private readonly IWindowSystem _windows;

    /// <summary>The window style the dialog asks Windows for when it is created: top-most for a question that must be seen.</summary>
    internal int RequestedExtendedStyle => CreateParams.ExStyle;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Read once by the base constructor, before the prompt is known; the window is created later, with the prompt.
            if (_prompt is not null && MustBeSeen)
            {
                parameters.ExStyle |= TopMostStyle;
            }
            return parameters;
        }
    }

    internal PortDialog(PortPrompt prompt, Func<int, bool> isInUse, Icon? icon, IWindowSystem? windows = null)
    {
        _prompt = prompt;
        _isInUse = isInUse;
        _windows = windows ?? Win32Windows.Instance;

        Text = "Weir";
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 14, 16, 12);
        // Weir is not running while this waits, and nothing else of Weir's is on screen, so it has to be findable: in the
        // taskbar, and in front (made so again when it is shown) rather than behind whatever else is open.
        ShowInTaskbar = MustBeSeen;
        TopMost = MustBeSeen;
        if (icon is not null)
        {
            Icon = icon;
        }

        const int width = 400;
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        var heading = new Label
        {
            Text = Heading(prompt),
            Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Regular),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(heading);

        layout.Controls.Add(Paragraph(
            $"The port is the number at the end of Weir's web address — where you open Weir in your browser, "
            + $"like http://localhost:{PortChoice.DefaultPort}.",
            width));

        var situation = Situation(prompt);
        if (situation is not null)
        {
            layout.Controls.Add(Paragraph(situation, width, bold: prompt.CurrentPortInUse));
        }

        var defaultBusy = prompt.Reason == PortPromptReason.Change
            ? PortChoice.DefaultPort != prompt.CurrentPort && isInUse(PortChoice.DefaultPort)
            : prompt.Reason == PortPromptReason.FirstRun
                ? prompt.CurrentPortInUse
                : isInUse(PortChoice.DefaultPort);

        _useDefault = new RadioButton
        {
            Text = defaultBusy
                ? $"Use Weir's default port, {PortChoice.DefaultPort} (in use by another program)"
                : $"Use Weir's default port, {PortChoice.DefaultPort}",
            AutoSize = true,
            Enabled = !defaultBusy,
            Margin = new Padding(0, 10, 0, 2),
        };
        layout.Controls.Add(_useDefault);

        var customRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 0),
        };
        _useCustom = new RadioButton { Text = "Use this port:", AutoSize = true, Margin = new Padding(0, 4, 4, 0) };
        _customPort = new TextBox { Width = 80, MaxLength = 5 };
        customRow.Controls.Add(_useCustom);
        customRow.Controls.Add(_customPort);
        layout.Controls.Add(customRow);

        _error = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 6, 0, 0),
            Visible = false,
        };
        layout.Controls.Add(_error);

        _address = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 10, 0, 0),
        };
        layout.Controls.Add(_address);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Text = prompt.Reason == PortPromptReason.Change ? "Restart on this port" : "Start Weir",
            AutoSize = true,
            MinimumSize = new Size(96, 0),
        };
        var cancel = new Button
        {
            Text = prompt.Reason == PortPromptReason.Change ? "Cancel" : "Quit",
            AutoSize = true,
            MinimumSize = new Size(80, 0),
            DialogResult = DialogResult.Cancel,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        // Pre-fill. The default when it is free and fits; otherwise the suggestion (a free
        // port above the busy one), or the current port when changing it.
        var prefill = prompt.Reason == PortPromptReason.Change ? prompt.CurrentPort : prompt.Suggested;
        if (!defaultBusy && (prefill is null || prefill == PortChoice.DefaultPort))
        {
            _useDefault.Checked = true;
        }
        else
        {
            _useCustom.Checked = true;
            _customPort.Text = prefill?.ToString(CultureInfo.InvariantCulture) ?? "";
        }

        // The two options sit in different layout containers (the custom one shares a row with
        // its text box), and WinForms only makes radio buttons exclusive within one container,
        // so the exclusivity is done here. Without it both stay ticked and the default wins.
        _useDefault.CheckedChanged += (_, _) =>
        {
            if (_useDefault.Checked)
            {
                _useCustom.Checked = false;
            }

            Refresh_();
        };
        _useCustom.CheckedChanged += (_, _) =>
        {
            if (_useCustom.Checked)
            {
                _useDefault.Checked = false;
            }

            Refresh_();
        };
        _customPort.TextChanged += (_, _) =>
        {
            if (!_useCustom.Checked)
            {
                _useCustom.Checked = true;
            }

            _error.Visible = false;
            _error.Text = "";
            Refresh_();
        };
        _customPort.Enter += (_, _) => { if (!_useCustom.Checked) { _useCustom.Checked = true; } };
        ok.Click += (_, _) => Accept();
        Shown += (_, _) =>
        {
            if (MustBeSeen)
            {
                ForegroundWindow.Bring(this, _windows);
            }
            else
            {
                Activate();
            }
            if (_useCustom.Checked)
            {
                _customPort.Focus();
                _customPort.SelectAll();
            }
            else
            {
                ok.Focus();
            }
        };

        Refresh_();
    }

    private static string Heading(PortPrompt prompt) => prompt.Reason switch
    {
        PortPromptReason.FirstRun => "Choose where Weir lives",
        PortPromptReason.SavedPortBusy => $"Port {prompt.CurrentPort} is in use",
        _ => "Change Weir's port",
    };

    private static string? Situation(PortPrompt prompt) => prompt.Reason switch
    {
        PortPromptReason.FirstRun when prompt.CurrentPortInUse && prompt.Suggested is { } s =>
            $"Another program on this computer is already using {PortChoice.DefaultPort}, Weir's default. "
            + $"Port {s} is free, so it is filled in below.",
        PortPromptReason.FirstRun when prompt.CurrentPortInUse =>
            $"Another program on this computer is already using {PortChoice.DefaultPort}, Weir's default. Enter a different port below.",
        PortPromptReason.FirstRun =>
            "Weir will remember your choice. You can change it later from the Weir icon in the taskbar.",
        PortPromptReason.SavedPortBusy =>
            $"Weir was set to use port {prompt.CurrentPort}, but another program is using it now. Weir will not switch "
            + "ports without asking you. Choose a different port, or quit, close the other program and start Weir again."
            + (prompt.Suggested is { } s2 ? $" Port {s2} is free, so it is filled in below." : ""),
        _ =>
            $"Weir is at port {prompt.CurrentPort} now. It will restart on the new port, and bookmarks to the old "
            + "address will stop working.",
    };

    private static Label Paragraph(string text, int width, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 0, 0, 6),
        Font = bold ? new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) : null,
    };

    private int? Selected() => _useDefault.Checked ? PortChoice.DefaultPort : PortChoice.ParsePort(_customPort.Text);

    private void Refresh_()
    {
        var port = Selected();
        _address.Text = port is { } p
            ? $"Weir will be at http://localhost:{p}/"
            : "Weir will be at http://localhost:<port>/";
    }

    private void Accept()
    {
        var allowed = _prompt.Reason == PortPromptReason.Change ? _prompt.CurrentPort : (int?)null;
        var text = _useDefault.Checked ? PortChoice.DefaultPort.ToString(CultureInfo.InvariantCulture) : _customPort.Text;
        var problem = PortChoice.Validate(text, allowed, _isInUse);
        if (problem is not null)
        {
            _error.Text = problem;
            _error.Visible = true;
            if (_useCustom.Checked)
            {
                _customPort.Focus();
                _customPort.SelectAll();
            }
            return;
        }

        ChosenPort = PortChoice.ParsePort(text);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Show the dialog modally and return the chosen port, or null if they quit.</summary>
    [ExcludeFromCodeCoverage]
    internal static int? Ask(PortPrompt prompt, Icon? icon)
    {
        using var dialog = new PortDialog(prompt, PortChoice.IsInUse, icon);
        return dialog.ShowDialog() == DialogResult.OK ? dialog.ChosenPort : null;
    }
}
