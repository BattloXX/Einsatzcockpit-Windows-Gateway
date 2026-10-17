using System.Diagnostics;
using System.Drawing.Drawing2D;
using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Status;

namespace Einsatzcockpit.PrintAgent.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AgentPaths _paths;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private AgentStatusSnapshot? _snapshot;
    private DateTimeOffset _statusWrittenAt = DateTimeOffset.MinValue;
    private bool _restarting;
    // Je Ampelfarbe genau ein Icon: GetHicon-Handles werden von Icon.Dispose nicht freigegeben,
    // ein Neuerzeugen alle 3 s würde GDI-Handles verbrauchen, bis der Tray abstürzt.
    private readonly Dictionary<TrayIndicator, Icon> _icons = new();

    public TrayApplicationContext(AgentPaths paths)
    {
        _paths = paths;
        _icon = new NotifyIcon
        {
            ContextMenuStrip = CreateMenu(),
            Visible = true,
            Text = "Einsatzcockpit Druckagent"
        };
        _icon.DoubleClick += (_, _) => ShowStatus();
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
        _timer.Start();
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status anzeigen", null, (_, _) => ShowStatus());
        menu.Items.Add("Protokoll anzeigen", null, (_, _) => new LogForm(_paths).Show());
        menu.Items.Add("Jetzt nach Updates suchen", null, (_, _) => RequestUpdate());
        menu.Items.Add("Datenordner öffnen", null, (_, _) => Open(_paths.DataDirectory));
        menu.Items.Add("Einsatzcockpit öffnen", null, (_, _) => Open(_snapshot?.CloudUrl));
        menu.Items.Add("Hilfe", null, (_, _) => new HelpForm().Show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitThread());
        return menu;
    }

    private void RefreshStatus()
    {
        var file = new StatusFile(_paths.StatusPath);
        var current = file.Read();
        if (File.Exists(_paths.StatusPath))
        {
            _statusWrittenAt = File.GetLastWriteTimeUtc(_paths.StatusPath);
        }

        var status = TrayStatusEvaluator.Evaluate(current, _statusWrittenAt, DateTimeOffset.UtcNow, _snapshot);
        _icon.Text = status.Tooltip;
        if (!_icons.TryGetValue(status.Indicator, out var icon))
        {
            icon = CreateIcon(status.Indicator);
            _icons[status.Indicator] = icon;
        }
        _icon.Icon = icon;
        foreach (var message in status.Events)
        {
            _icon.ShowBalloonTip(5000, "Einsatzcockpit Druckagent", message, ToolTipIcon.Info);
        }

        _snapshot = current;
        if (File.Exists(_paths.UpdatePendingPath) && !_restarting)
        {
            _restarting = true;
            _icon.Text = "Einsatzcockpit Druckagent – Update wird installiert …";
            StartAfterUpdate();
            var close = new System.Windows.Forms.Timer { Interval = 5000 };
            close.Tick += (_, _) => ExitThread();
            close.Start();
        }
    }

    private static Icon CreateIcon(TrayIndicator indicator)
    {
        var color = indicator switch
        {
            TrayIndicator.Green => Color.ForestGreen,
            TrayIndicator.Yellow => Color.Goldenrod,
            _ => Color.Firebrick
        };
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 1, 1, 30, 30);
        using var font = new Font(SystemFonts.DefaultFont.FontFamily, 17, FontStyle.Bold);
        TextRenderer.DrawText(graphics, "D", font, new Rectangle(7, 5, 22, 24), Color.White);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void ShowStatus()
    {
        new StatusForm(_paths, _snapshot).Show();
    }

    private void RequestUpdate()
    {
        Directory.CreateDirectory(_paths.RequestsDirectory);
        File.WriteAllText(_paths.UpdateRequestPath, DateTimeOffset.UtcNow.ToString("O"));
        _icon.ShowBalloonTip(3000, "Einsatzcockpit Druckagent", "Updateprüfung wurde angefordert.", ToolTipIcon.Info);
    }

    private void StartAfterUpdate()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EinsatzcockpitPrintAgent.exe");
        var command = "Start-Sleep -Seconds 2; $end=(Get-Date).AddMinutes(10); "
            + $"while ((Test-Path '{_paths.UpdatePendingPath}') -and ((Get-Date) -lt $end)) {{ Start-Sleep 2 }}; "
            + $"if (-not (Test-Path '{_paths.UpdatePendingPath}')) {{ "
            + $"Start-Process -FilePath '{exe}' -ArgumentList 'tray' }}";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{command}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void Open(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        base.ExitThreadCore();
    }
}
