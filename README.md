# Einsatzcockpit Windows-Druckagent

Nativer Windows-Dienst (.NET 10, ohne Docker). Er gibt Druckaufträge aus
[Einsatzcockpit](https://github.com/BattloXX/Einsatzcockpit) über den Windows-Druckspooler aus.
Das ist die Ergänzung zum Linux-Gateway (`Einsatzcockpit-gateway`) für Standorte, an denen nur
Windows-PCs stehen.

Typischer Einsatz ist der **Kommandofunk**: Zwei PCs bedienen denselben Netzwerkdrucker
redundant. Fällt ein PC aus oder ist sein Drucker nicht erreichbar, druckt der andere.
Doppelt gedruckt wird dabei nie blind.

Weitere Szenarien funktionieren mit demselben Modell:

- ein PC mit einem oder mehreren Druckern
- USB-Drucker
- Ausweichdrucker
- PDF-Ablage in einen Ordner (Archiv, Fax-Hotfolder)
- IPP-Druck ohne Treiber
- externes Druckprogramm

Spezifikation (Protokoll, Lease, Zustandsmaschine, Migration):
[docs/print-agent-architektur.md](docs/print-agent-architektur.md)

## Funktionsweise

- **Verbindung:** nur ausgehend, per WSS zu `/ws/print-agent`. Die Authentifizierung läuft über
  ein individuelles Device-Token je PC. Es gibt keine Portfreigaben und keine Anmeldung am PC.
- **Lease statt Push:** Die Cloud vergibt jeden Auftrag atomar an genau einen Agent. Vorrang hat
  der Agent mit der kleinsten Priorität, dessen Drucker bereit ist. Ein Agent mit niedrigerer
  Priorität übernimmt nur, wenn der bessere nicht bereit ist oder nicht zugreift.
- **Druckfreigabe:** Der Agent übergibt erst an den Spooler, wenn die Cloud seinen `print_intent`
  bestätigt hat. Der Zustand `submitting` wird lokal gespeichert, **bevor** der Spooler
  aufgerufen wird.
- **Neustart von Windows mitten im Druck:** Der Agent sucht den Auftrag in der Warteschlange
  über den Dokumentnamen `ECPG-<job>-g<generation>`. Findet er ihn, überwacht er ihn weiter.
  Findet er ihn nicht, meldet er **„Unklar – bitte prüfen“**. Ein Mensch entscheidet dann in
  der Druckhistorie: *Wurde gedruckt*, *Erneut drucken* oder *Verwerfen*.
- **Lokale Auftragsverwaltung:** SQLite in `%ProgramData%\Einsatzcockpit\PrintAgent\agent.db`.
  Statusmeldungen laufen über eine Outbox mit Bestätigung und gehen auch nach einem Neustart
  nicht verloren.
- **Druckwege je Route:**

  | Druckweg | Wirkung |
  |---|---|
  | `driver` | PDF mit PDFium rastern und über den Windows-Druckertreiber ausgeben (Standard, jeder Drucker). Kopien, Duplex, A3/A4, Fach und Farbe über den Treiber. |
  | `pdf_direct` | PDF als RAW mit PJL-Kopf über den Spooler (schnell, für PDF-fähige Geräte) |
  | `ipp_direct` | IPP-Auftrag direkt an den Drucker |
  | `folder` | PDF in einen Ordner ablegen |
  | `command` | externes Programm. Nur Programme, die lokal in `agent.json` erlaubt sind. |

- **Karten** (Lagekarte, Einsatzstellen-Blatt usw.) rendert der Agent mit dem vorhandenen
  Microsoft Edge (headless). Er wartet auf `window.__ecpgReady`, genau wie das Linux-Gateway.

## Installation

1. In Einsatzcockpit unter **Gateway / Druck → + Gateway anlegen** den Typ *Windows-Druckagent*
   wählen (je PC ein Eintrag) und einen **Pairing-Code erzeugen**.
2. Den Netzwerkdrucker auf dem PC als **lokale Warteschlange mit TCP/IP-Anschluss** einrichten,
   nicht als Benutzer-Verbindung `\\server\drucker`. Nur so ist er für den Dienst ohne
   Anmeldung sichtbar.
3. ZIP aus den Releases entpacken und PowerShell **als Administrator** öffnen:

   ```powershell
   .\install.ps1 -CloudUrl https://einsatzcockpit.example.at -PairingCode ABCD1234
   ```

   Der Dienst `EinsatzcockpitPrintAgent` startet automatisch (verzögert, nach dem Spooler) und
   ohne Benutzeranmeldung. Nach einem Absturz startet er nach 10, 30 bzw. 60 Sekunden neu.
4. In Einsatzcockpit erscheint das Inventar des PCs. Die Warteschlange einem Druckziel zuordnen,
   z. B. *Kommandofunk*: PC A mit Priorität 10, PC B mit Priorität 20 auf **dasselbe** Druckziel.
5. Druckregeln und Druckdialoge verwenden das Druckziel wie jeden anderen Drucker.

Updates werden standardmäßig automatisch geprüft (zwei Minuten nach Start, danach alle sechs
Stunden). Ein angemeldeter Benutzer darf die lesbare `status.json` und `logs\` ansehen sowie
`requests\update-request` für eine sofortige Prüfung anlegen; `token.bin` und `agent.db`
bleiben ausschließlich für den Dienst zugänglich.
Deinstallation: `install.ps1 -Uninstall`. Daten und Token bleiben dabei erhalten.

Für Druckerfreigaben eines Druckservers kann der Dienst unter einem eigenen Konto laufen:
`install.ps1 -ServiceAccount DOMAIN\svc-druck`.

## Befehle

```text
EinsatzcockpitPrintAgent.exe pair --url <cloud> --code <CODE>   koppeln
EinsatzcockpitPrintAgent.exe status                             Kopplung, Jobs je Zustand, letzte Jobs
EinsatzcockpitPrintAgent.exe unpair                             Token löschen
```

## Konfiguration `%ProgramData%\Einsatzcockpit\PrintAgent\agent.json`

```json
{
  "cloud_url": "https://einsatzcockpit.example.at",
  "proxy": null,
  "log_level": "Information",
  "allowed_commands": ["C:\\Tools\\SumatraPDF.exe"],
  "edge_path": null,
  "html_ready_timeout_s": 9,
  "auto_update": true,
  "update_interval_h": 6,
  "update_repo": "BattloXX/Einsatzcockpit-Windows-Gateway"
}
```

Routen, Prioritäten und Druckoptionen werden zentral in der Web-Oberfläche gepflegt und per
`config_sync` verteilt.

Logs liegen im Windows-Ereignisprotokoll (Anwendung, Quelle „Einsatzcockpit Druckagent“) und in
`logs\` im Datenverzeichnis (14 Tage).

## Tray-Symbol

Nach der Anmeldung startet ein Tray-Symbol im Benutzerkontext. Grün bedeutet verbunden und bereit,
gelb weist auf nicht bereite Routen, unklare Aufträge oder ein fehlgeschlagenes Update hin, rot auf
eine getrennte Verbindung, einen gestoppten Dienst oder eine veraltete Statusdatei. Das Menü zeigt
Status, Protokoll und Hilfe, öffnet Datenordner oder Einsatzcockpit und kann eine Updateprüfung
anfordern. „Beenden“ beendet nur das Tray-Symbol, nicht den Windows-Dienst.

## Automatische Updates

`auto_update` und `update_interval_h` in `agent.json` steuern die automatische Prüfung. Updates
werden bei laufenden Druckübergaben pausiert und nach einer erfolgreichen Gesundheitsprüfung
übernommen; andernfalls wird auf die vorherige EXE zurückgerollt. Fehlgeschlagene Versionen stehen
in `update-failed.json` und werden nicht erneut automatisch installiert.

## Entwicklung

```bash
dotnet build Einsatzcockpit.PrintAgent.slnx
dotnet test tests/Einsatzcockpit.PrintAgent.Tests
dotnet publish src/Einsatzcockpit.PrintAgent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Die Projektaufteilung:

- `Einsatzcockpit.PrintAgent.Core` (net10.0, plattformneutral): Protokoll, Zustandsmaschine,
  Recovery, Outbox, Cloud-Verbindung. Die Tests dazu laufen unter Linux.
- `Einsatzcockpit.PrintAgent` (net10.0-windows): Spooler, Druckwege, Inventar und Dienst. Es
  wird in der CI auf `windows-latest` gebaut und als Single-File-ZIP paketiert.

## Lizenz

LGPL-2.1, wie das Linux-Gateway.
