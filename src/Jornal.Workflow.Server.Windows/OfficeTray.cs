using System.Runtime.InteropServices;

namespace Jornal.Workflow.Server.Windows;

internal sealed class OfficeTray : IDisposable
{
    private readonly Icon _ready = MakeIcon(Color.FromArgb(36, 129, 75));
    private readonly Icon _problem = MakeIcon(Color.FromArgb(177, 108, 0));
    private readonly Icon _stopped = MakeIcon(Color.FromArgb(91, 102, 114));
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _power;
    private readonly ToolStripMenuItem _journal;

    public OfficeTray(Action open, Func<Task> power, Func<Task> journal, Action closePanel)
    {
        _menu.Items.Add("Открыть", null, (_, _) => open());
        _power = new ToolStripMenuItem("Запустить", null, async (_, _) => await power());
        _journal = new ToolStripMenuItem("Журнал", null, async (_, _) => await journal());
        _menu.Items.Add(_power);
        _menu.Items.Add(_journal);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Закрыть панель", null, (_, _) => closePanel());
        _tray.ContextMenuStrip = _menu;
        _tray.DoubleClick += (_, _) => open();
        Update(false, false, true, false);
        _tray.Visible = true;
    }

    public void Update(bool running, bool ready, bool canToggle, bool hasJournal)
    {
        _power.Text = running ? "Остановить" : "Запустить";
        _power.Enabled = canToggle;
        _journal.Enabled = hasJournal;
        _tray.Icon = running ? ready ? _ready : _problem : _stopped;
        _tray.Text = running ? ready ? "Сервер заявок — работает" : "Сервер заявок — проверьте состояние" : "Сервер заявок — остановлен";
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _ready.Dispose();
        _problem.Dispose();
        _stopped.Dispose();
    }

    private static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillEllipse(brush, 2, 2, 28, 28);
            graphics.FillRectangle(Brushes.White, 8, 9, 16, 5);
            graphics.FillRectangle(Brushes.White, 8, 17, 16, 5);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
