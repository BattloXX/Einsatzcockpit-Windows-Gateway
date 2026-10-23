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

Die Version **2026.10.17-win-x64** enthaelt Fehler im Installer und beim Lesen der
Pairing-Antwort. Fuer eine Erstinstallation ist ein neueres, korrigiertes ZIP aus
[Releases](https://github.com/BattloXX/Einsatzcockpit-Windows-Gateway/releases) erforderlich.
Die folgende Anleitung gilt fuer Pakete mit `Installieren.cmd` und `Install.Common.ps1`.
Der aktuelle korrigierte Release ist **2026.10.23.1**. Bereits heruntergeladene ZIP-Dateien
der Version 2026.10.17 werden dadurch nicht repariert; das neue Paket herunterladen.

1. Windows x64, Administratorrechte und ausgehendes HTTPS/WSS zur Cloud ueber Port 443
   bereitstellen. .NET ist im Paket enthalten. Fuer HTML-/Kartendruck muss Microsoft Edge
   installiert sein. Netzwerkdrucker als **lokale TCP/IP-Warteschlange** einrichten und
   mit einer Windows-Testseite pruefen.
2. Das Windows-x64-ZIP vollstaendig in einen lokalen Ordner entpacken. Es muss neben der
   EXE auch `install.ps1`, `Install.Common.ps1`, `Installieren.cmd` und `update.ps1` enthalten.
3. In Einsatzcockpit unter **Gateway / Druck → + Gateway anlegen** den Typ
   **Windows-Druckagent** waehlen (je PC ein Eintrag), dann **Pairing-Code erzeugen**.
   Der Code gilt zehn Minuten und kann nur einmal verwendet werden.
4. Fuer `https://einsatzcockpit.com`: Rechtsklick auf **Installieren.cmd → Als Administrator
   ausfuehren**. Den Code eingeben; die Eingabe bleibt verdeckt. Alternativ PowerShell
   **als Administrator** im entpackten Ordner oeffnen:

   ```powershell
   .\install.ps1 -CloudUrl "https://einsatzcockpit.com" -PairingCode "<PAIRING-CODE>"
   ```

   Bei anderer Cloud-Adresse die URL ersetzen. Ohne `-PairingCode` wird der Code verdeckt
   abgefragt. Das vermeidet einen Code im PowerShell-Befehlsverlauf:

   ```powershell
   .\install.ps1 -CloudUrl "https://einsatzcockpit.com"
   ```

   **Ein** Bindestrich vor PowerShell-Parametern; `-Code` ist ein Alias fuer `-PairingCode`.
   Positionsargumente und `--PairingCode`/`--code` werden abgewiesen, bevor etwas geaendert wird.
   Die EXE hat eine eigene CLI: deren `pair --url ... --code ...` ist kein Installer-Aufruf.
5. Die vier Installationsschritte abwarten. Erfolg wird erst gemeldet, wenn der Dienst
   laeuft und eine **frische** Statusdatei seine Cloud-Verbindung bestaetigt (bis zu 60 s).
   In Einsatzcockpit die Seite neu laden: Agent **Online**, Inventar sichtbar.
6. Die Warteschlange einem Druckziel zuordnen, dann am Druckziel **Testseite** drucken
   und den Ausdruck kontrollieren. Fuer Redundanz PC A mit Prioritaet 10 und PC B mit
   Prioritaet 20 demselben Druckziel zuordnen.

Der Dienst `EinsatzcockpitPrintAgent` laeuft standardmaessig als **LocalSystem**, ohne
Windows-Passwort und ohne Benutzeranmeldung. Autostart ist verzoegert, abhaengig vom Spooler.
Nach Absturz erfolgt ein Neustart nach 10, 30 bzw. 60 Sekunden.

### Updates, Deinstallation und Dienstkonten

Ein manuelles Update mit dem neuen entpackten ZIP als Administrator ausfuehren:

```powershell
.\install.ps1
```

Wenn bereits gekoppelt, werden Cloud-Adresse, Token, Auftragsdaten und Dienstkonto
beibehalten; kein neuer Code und kein Windows-Passwort erforderlich. Nicht mitten in einer
Druckuebergabe aktualisieren; unklare Auftraege anschliessend in der Druckhistorie pruefen.
Das bestehende automatische Update mit SHA256-Pruefung, Update-Sperre bei laufenden
Druckuebergaben und Gesundheitspruefung bleibt erhalten.

Deinstallation im entpackten ZIP oder im Installationsordner:

```powershell
.\install.ps1 -Uninstall
```

Daten, Konfiguration und DPAPI-geschuetztes Token unter
`%ProgramData%\Einsatzcockpit\PrintAgent` bleiben erhalten. Erneute Installation auf
**demselben Windows-PC** verwendet sie weiter. Zum endgueltigen Entkoppeln zuerst den
Dienst stoppen und das Token in Einsatzcockpit widerrufen. Ein Token laesst sich wegen
DPAPI nicht auf einen anderen PC uebertragen.

Nur fuer Druckserver-Freigaben oder besondere Netzwerkberechtigungen ein eigenes Konto
angeben (Recht **Als Dienst anmelden** muss vorhanden sein):

```powershell
.\install.ps1 -ServiceAccount 'DOMAIN\svc-druck'
# Passwort wird als SecureString abgefragt, getrennt vom Pairing-Code.
# Fuer unbeaufsichtigte Installation: -NonInteractive und ein bereits vorhandenes
# SecureString mit -ServicePassword uebergeben; Passwort nie im Klartext-Befehl speichern.
```

Das Installer-Skript gibt dem ausgewaehlten Dienstkonto Zugriff auf Daten und Token.
Das DPAPI-Token bleibt auf SYSTEM, Administratoren und dieses Konto beschraenkt.
Die CLI `pair` direkt als Administrator setzt die Token-ACL wieder auf SYSTEM und
Administratoren; bei eigenem Dienstkonto deshalb immer ueber den Installer koppeln.
Ein angemeldeter Benutzer kann `status.json` und `logs\` lesen sowie ueber
`requests\update-request` eine Updatepruefung anfordern.

### Fehlerbehebung

| Meldung / Symptom | Loesung |
|---|---|
| `NamedParameterNotFound` / `ServicePassword`-Konvertierung | Korrigiertes ZIP verwenden; PowerShell-Parameter mit einem Bindestrich, z. B. `-PairingCode`, niemals positionsbasiert uebergeben. |
| Administratorrechte fehlen | Installieren.cmd per Rechtsklick als Administrator starten oder Administrator-PowerShell verwenden. |
| Skriptausfuehrung blockiert | In Administrator-PowerShell: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -CloudUrl "https://einsatzcockpit.com"`. Dies gilt nur fuer diesen Prozess; Organisationsrichtlinien gelten weiter. |
| Pairing-Code fehlt / ungueltig / abgelaufen / verwendet | Neuen Code in Einsatzcockpit erzeugen. Kein Windows-Passwort eingeben. Fuer unbeaufsichtigte Aufrufe `-NonInteractive -PairingCode ...` verwenden. |
| Dienst installiert, Cloud nicht bestaetigt | Tray-Status, Internet, Cloud-URL, ausgehendes HTTPS/WSS 443 und Proxy in agent.json pruefen. Erneut ohne Code installieren, wenn bereits gekoppelt. Ein laufender Dienst allein bedeutet noch keine Cloud-Verbindung. |
| Dienstkonto kann nicht starten | Passwort, Recht „Als Dienst anmelden“ und Druckerberechtigungen pruefen; Standard LocalSystem braucht kein Passwort. |
| Kein Inventar / keine Drucker | Lokale TCP/IP-Queue statt benutzergebundener Freigabe einrichten; Spooler pruefen. |
| Dienst zum Loeschen vorgemerkt | Dienste-Fenster schliessen, ggf. Windows neu starten, dann erneut installieren. |
| HTML-/Kartendruck scheitert | Microsoft Edge installieren oder edge_path in agent.json setzen; PDF-Testseite getrennt pruefen. |

Pairing-Codes und Zugangsdaten nicht in Support-Logs oder Screenshots weitergeben.
Der Installer erstellt kein Transkript; Codeuebergabe an die EXE erfolgt ueber stdin,
Dienstpasswoerter werden ueber die Windows-Verwaltungsschnittstelle gesetzt.

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


## Verbindung und Tray: korrigiert ab 2026.10.23.1

Die Cloud sendet Agent- und Job-IDs numerisch. Der Agent konvertiert diese jetzt gezielt
zwischen JSON-Zahlen und lokalen SQLite-Strings; Tokens und Spool-Referenzen bleiben Strings.
Empfangsfehler werden nicht mehr still geschluckt. Nach Netzunterbrechung sendet der Reconnect
Hello und Inventar erneut. Erst ein gueltiges config_sync bestaetigt die lokale Verbindung.

Das Tray-Symbol startet ohne Schreibversuch auf geschuetzte ProgramData-Dienstverzeichnisse.
Es bleibt eine Anwendung der angemeldeten Windows-Sitzung; LocalSystem und Session 0
sind kein Desktop fuer ein sichtbares Tray-Symbol. HKLM-Run startet es bei Anmeldung.
Nach Installation von 2026.10.23.1 einmal ab-/anmelden oder als normaler Benutzer
`"C:\Program Files\Einsatzcockpit\PrintAgent\EinsatzcockpitPrintAgent.exe" tray` starten
(in PowerShell vor dem zitierten Programmpfad `&` verwenden). Den Dienst nicht auf ein
Benutzerkonto umstellen. Im Windows-Menue fuer ausgeblendete Taskleistensymbole nachsehen.

Cloud-Deployment mit Migration 0306 und neu gestarteten Workern ist fuer die gemeinsame
Presence und Diagnose erforderlich. Dieser Agent-Release enthaelt den Protokoll-,
Reconnect- und Tray-Bugfix; der bisherige Release 2026.10.23 enthaelt ihn noch nicht. Details und Abnahme: Cloud-Dokumentation `docs/print-agent-architektur.md`.
