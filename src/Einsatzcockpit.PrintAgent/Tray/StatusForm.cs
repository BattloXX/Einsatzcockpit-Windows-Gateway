using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Status;

namespace Einsatzcockpit.PrintAgent.Tray;

public sealed class StatusForm : Form
{
    private readonly AgentPaths _paths;
    private readonly Label _header = new()
    {
        AutoSize = true,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
    };
    private readonly Label _identity = new() { AutoSize = true };
    private readonly ListView _routes = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly Label _jobs = new() { AutoSize = true };
    private readonly Label _update = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    public StatusForm(AgentPaths paths, AgentStatusSnapshot? snapshot)
    {
        _paths = paths;
        Text = "Einsatzcockpit Druckagent – Status";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(700, 480);
        Size = new Size(850, 600);
        _routes.Columns.AddRange(new[] { "Druckziel", "Warteschlange", "Druckweg", "Priorität", "Bereit", "Grund" }
            .Select(text => new ColumnHeader { Text = text, Width = 125 }).ToArray());
        var updateButton = new Button { Text = "Jetzt nach Updates suchen", AutoSize = true };
        updateButton.Click += (_, _) => RequestUpdate();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_header, 0, 0);
        layout.Controls.Add(_identity, 0, 1);
        layout.Controls.Add(_routes, 0, 2);
        layout.Controls.Add(_jobs, 0, 3);
        var updatePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        updatePanel.Controls.Add(_update);
        updatePanel.Controls.Add(updateButton);
        layout.Controls.Add(updatePanel, 0, 4);
        Controls.Add(layout);
        _timer.Tick += (_, _) => RefreshSnapshot();
        FormClosed += (_, _) => _timer.Dispose();
        SetSnapshot(snapshot ?? new StatusFile(_paths.StatusPath).Read());
        _timer.Start();
    }

    private void RefreshSnapshot()
    {
        SetSnapshot(new StatusFile(_paths.StatusPath).Read());
    }

    private void SetSnapshot(AgentStatusSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _header.Text = "Getrennt – keine Statusdatei verfügbar";
            _header.ForeColor = Color.Firebrick;
            return;
        }

        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_paths.StatusPath);
        var connected = snapshot.Connected && age <= TimeSpan.FromSeconds(30);
        _header.Text = (connected ? "Verbunden seit " : "Getrennt seit ")
            + (snapshot.LastConnectedAt ?? snapshot.UpdatedAt).ToLocalTime().ToString("g");
        _header.ForeColor = connected ? Color.ForestGreen : Color.Firebrick;
        _identity.Text = $"{snapshot.AgentName ?? "Unbenannter Agent"} · Version {snapshot.Version} · "
            + (snapshot.CloudUrl ?? "Keine Cloud-URL");
        _routes.BeginUpdate();
        _routes.Items.Clear();
        foreach (var route in snapshot.Routes)
        {
            var item = new ListViewItem(route.PrinterName ?? "Druckziel");
            item.SubItems.Add(route.LocalQueue);
            item.SubItems.Add(route.RenderMode);
            item.SubItems.Add(route.Priority?.ToString() ?? "–");
            item.SubItems.Add(route.Ready ? "✓" : "✕");
            item.SubItems.Add(route.Detail ?? route.State);
            _routes.Items.Add(item);
        }
        _routes.EndUpdate();
        _jobs.Text = $"Aufträge: offen {snapshot.OpenJobs}, unklar {snapshot.UncertainJobs}. "
            + "Unklare Aufträge in Einsatzcockpit unter Gateway → Druckhistorie prüfen.";
        _update.Text = $"Update: {UpdateLabel(snapshot.Update.State)}; verfügbar: {snapshot.Update.AvailableVersion ?? "–"}; "
            + $"letzte Prüfung: {snapshot.Update.LastCheckAt?.ToLocalTime().ToString("g") ?? "–"}; "
            + (snapshot.Update.Message ?? string.Empty);
    }

    private void RequestUpdate()
    {
        Directory.CreateDirectory(_paths.RequestsDirectory);
        File.WriteAllText(_paths.UpdateRequestPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static string UpdateLabel(string? state) => state switch
    {
        "idle" => "noch nicht geprüft",
        "checking" => "wird geprüft",
        "available" => "neue Version verfügbar",
        "downloading" => "wird geladen",
        "installing" => "wird installiert",
        "failed" => "fehlgeschlagen",
        "up_to_date" => "aktuell",
        "postponed" or "verschoben" => "verschoben (Druckauftrag läuft)",
        "disabled" => "automatische Updates aus",
        _ => state ?? "–",
    };
}
