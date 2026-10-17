using System.Diagnostics;
using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Status;

namespace Einsatzcockpit.PrintAgent.Tray;

public sealed class LogForm : Form
{
    private readonly AgentPaths _paths;
    private readonly ComboBox _files = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _search = new() { Width = 180, PlaceholderText = "Suchen" };
    private readonly RichTextBox _content = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false };
    private readonly CheckBox _follow = new() { Text = "Automatisch mitlaufen", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    public LogForm(AgentPaths paths)
    {
        _paths = paths;
        Text = "Einsatzcockpit Druckagent – Protokoll";
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(950, 650);
        MinimumSize = new Size(650, 400);
        _content.Font = new Font(FontFamily.GenericMonospace, 9);
        _filter.Items.AddRange(["Alle", "Warnungen + Fehler", "Nur Fehler"]);
        _filter.SelectedIndex = 0;
        var copy = new Button { Text = "In Zwischenablage kopieren", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(_content.Text);
        var open = new Button { Text = "Ordner öffnen", AutoSize = true };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(_paths.LogDirectory) { UseShellExecute = true });
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.AddRange([_files, _filter, _search, _follow, copy, open]);
        Controls.Add(_content);
        Controls.Add(top);
        _files.SelectedIndexChanged += (_, _) => LoadLog();
        _filter.SelectedIndexChanged += (_, _) => LoadLog();
        _search.TextChanged += (_, _) => LoadLog();
        _timer.Tick += (_, _) => { if (_follow.Checked) { LoadLog(); } };
        FormClosed += (_, _) => _timer.Dispose();
        RefreshFiles();
        _timer.Start();
    }

    private void RefreshFiles()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        var selected = _files.SelectedItem?.ToString();
        var files = Directory.EnumerateFiles(_paths.LogDirectory, "*.log")
            .Where(path => Path.GetFileName(path).StartsWith("agent-", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith("update-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        _files.Items.Clear();
        _files.Items.AddRange(files);
        if (files.Length > 0)
        {
            _files.SelectedItem = files.Contains(selected) ? selected : files[0];
        }
    }

    private void LoadLog()
    {
        if (_files.SelectedItem is not string path || !File.Exists(path))
        {
            RefreshFiles();
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = LogFilter.Tail(ReadLines(reader), 2000)
                .Where(line => LogFilter.Matches(line, (LogFilterMode)_filter.SelectedIndex, _search.Text));
            _content.Clear();
            foreach (var line in lines)
            {
                _content.SelectionColor = LogFilter.GetLevel(line) switch
                {
                    LogLevelKind.Error => Color.Firebrick,
                    LogLevelKind.Warning => Color.DarkOrange,
                    _ => _content.ForeColor
                };
                _content.AppendText(line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }
}
