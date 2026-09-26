using System.Windows.Forms;
using Drawing = System.Drawing;

namespace ConnectionClue.App;

internal enum TrayState { Normal, Warning, Problem }

/// <summary>
/// Notification-area icon via WinForms NotifyIcon (WPF has none; avoids a third-party package).
/// Status badges differ by shape (! vs ×) as well as colour, and the tooltip carries the text.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _check, _background;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons = new()
    {
        [TrayState.Normal] = Load("ConnectionClue.ico"),
        [TrayState.Warning] = Load("TrayWarning.ico"),
        [TrayState.Problem] = Load("TrayProblem.ico"),
    };

    public TrayIcon(string open, string check, string background, string exit, bool rightToLeft)
    {
        var menu = new ContextMenuStrip { RightToLeft = rightToLeft ? RightToLeft.Yes : RightToLeft.No };
        var openItem = new ToolStripMenuItem(open, null, (_, _) => Open?.Invoke(this, EventArgs.Empty));
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        _check = new ToolStripMenuItem(check, null, (_, _) => CheckNow?.Invoke(this, EventArgs.Empty));
        _background = new ToolStripMenuItem(background, null, (_, _) => ToggleBackground?.Invoke(this, EventArgs.Empty));
        menu.Items.AddRange([openItem, _check, _background, new ToolStripSeparator(),
            new ToolStripMenuItem(exit, null, (_, _) => Exit?.Invoke(this, EventArgs.Empty))]);
        _icon = new NotifyIcon { Icon = _icons[TrayState.Normal], Text = "ConnectionClue", ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Open?.Invoke(this, EventArgs.Empty); };
        _icon.BalloonTipClicked += (_, _) => Open?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Open, CheckNow, ToggleBackground, Exit;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void Update(TrayState state, string tooltip, bool canCheck, bool backgroundOn)
    {
        _icon.Icon = _icons[state];
        _icon.Text = Truncate(tooltip, 127); // NotifyIcon limit
        _check.Enabled = canCheck;
        _background.Checked = backgroundOn;
    }

    /// <summary>Shown as a Windows notification (respects Focus / Do not disturb).</summary>
    public void Notify(string title, string text, bool warning) =>
        _icon.ShowBalloonTip(10_000, title, Truncate(text, 250), warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Drawing.Icon Load(string name)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}")).Stream;
        return new Drawing.Icon(stream, SystemInformation.SmallIconSize);
    }
}
