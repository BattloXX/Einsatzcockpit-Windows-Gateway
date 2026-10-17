using System.Diagnostics;
using System.Reflection;

namespace Einsatzcockpit.PrintAgent.Tray;

public sealed class HelpForm : Form
{
    public HelpForm()
    {
        Text = "Einsatzcockpit Druckagent – Hilfe";
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(760, 620);
        MinimumSize = new Size(550, 400);
        var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
        text.Text = "Einsatzcockpit Druckagent\n\n"
            + "Was macht der Agent?\nEr nimmt Druckaufträge aus Einsatzcockpit entgegen und übergibt sie "
            + "sicher an lokale Drucker.\n\n"
            + "Farben\nGrün: verbunden und bereit. Gelb: Route nicht bereit, unklare Aufträge oder "
            + "fehlgeschlagenes Update. Rot: getrennt, Dienst gestoppt oder Status älter als 30 Sekunden.\n\n"
            + "Kopplung\nAls Administrator ausführen: EinsatzcockpitPrintAgent.exe pair --url <Cloud> "
            + "--code <Code>. Den Code erzeugen Sie in Einsatzcockpit unter Gateway / Druck.\n\n"
            + "Drucker richtig einrichten\nVerwenden Sie eine lokale TCP/IP-Warteschlange, keine "
            + "\\\\server-Freigabe.\n\n"
            + "Redundanz\nZwei PCs können dasselbe Druckziel mit unterschiedlicher Priorität bedienen. "
            + "Bei Ausfall weicht der Agent automatisch aus.\n\n"
            + "Unklar – bitte prüfen\nÖffnen Sie Gateway → Druckhistorie und entscheiden Sie, ob der "
            + "Auftrag gedruckt wurde, erneut gedruckt oder verworfen wird.\n\n"
            + "Automatische Updates\nUpdates werden laut agent.json (Standard: alle 6 Stunden) geprüft. Bei laufenden "
            + "Druckübergaben wird pausiert; bei fehlender Gesundheitsprüfung erfolgt ein Rollback. "
            + "Fehler stehen in update-failed.json.\n\n"
            + "Fehlersuche\nDienst über services.msc neu starten, Protokoll öffnen oder den Befehl status "
            + "ausführen.";
        var readme = new LinkLabel { Text = "README öffnen", AutoSize = true };
        readme.Click += (_, _) => Open("https://github.com/BattloXX/Einsatzcockpit-Windows-Gateway#readme");
        var releases = new LinkLabel { Text = "Releases", AutoSize = true };
        releases.Click += (_, _) => Open("https://github.com/BattloXX/Einsatzcockpit-Windows-Gateway/releases");
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unbekannt";
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bottom.Controls.AddRange([readme, releases, new Label { Text = "Version " + version, AutoSize = true }]);
        Controls.Add(text);
        Controls.Add(bottom);
    }

    private static void Open(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
