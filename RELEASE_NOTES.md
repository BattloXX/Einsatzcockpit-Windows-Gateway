## Windows-Druckagent 2026.10.23.1: Verbindung und Tray korrigiert

- Numerische Agent- und Job-IDs der ECP-Cloud korrekt lesen und zurueckmelden. Dies behebt den JSON-Fehler bei config_sync und den Vertragsfehler bei Druckjobmeldungen.
- Online-Status lokal erst nach gueltiger Cloud-Konfiguration bestaetigen.
- Empfang und Heartbeats gemeinsam ueberwachen; bei Unterbrechung automatisch neu verbinden und Hello sowie Inventar erneut senden.
- Ping/Pong bestaetigt die Erreichbarkeit auch bei pausierter Druckjob-Abfrage. Verbindungsoperationen haben Zeitlimits.
- Strukturierte Verbindungsdiagnose ohne Ausgabe von Tokens, Zugangsdaten oder rohen Fehlerantworten.
- Tray-Symbol im angemeldeten Benutzerkontext starten, ohne geschuetzte Dienstverzeichnisse anzulegen. LocalSystem bleibt das Standard-Dienstkonto.

### Bestehenden Agent aktualisieren

Das neue Windows-x64-ZIP vollstaendig entpacken und im entpackten Ordner eine PowerShell **als Administrator** oeffnen:

```powershell
.\install.ps1
```

Vorhandenes Token, Dienstkonto, Konfiguration und Auftragsdaten bleiben erhalten. Kein neues Pairing erforderlich. Der automatische Updater bietet diese Version ebenfalls nach seinen bestehenden Regeln an.

Fuer das Tray-Symbol einmal ab-/anmelden oder die EXE als angemeldeter Benutzer starten:

```powershell
& "C:\Program Files\Einsatzcockpit\PrintAgent\EinsatzcockpitPrintAgent.exe" tray
```

### Erstinstallation

`Installieren.cmd` per Rechtsklick **als Administrator ausfuehren** und den Pairing-Code verdeckt eingeben. Alternativ:

```powershell
.\install.ps1 -CloudUrl "https://einsatzcockpit.com" -PairingCode "<PAIRING-CODE>"
```

### Cloud-Seite und Verifikation

Die vollstaendige Korrektur der workeruebergreifenden Online-Anzeige benoetigt zusaetzlich den ECP-Cloud-Fix aus [PR #715](https://github.com/BattloXX/Einsatzcockpit/pull/715), Migration 0306 und neu gestartete Cloud-Worker. Die dort ebenfalls ergaenzte Gateway-Auswahl fuer Offline-Alarme benoetigt Migration 0307; sie ist eine Cloud-Funktion.

Vor dem Release erfolgreich geprueft: 68 .NET-Tests unter Linux und Windows, 20 Installer-Tests je PowerShell-Umgebung (Linux sowie Windows PowerShell 5.1 und 7) und echter Windows-Tray-Prozessstart mit explizit verweigertem Schreibzugriff auf den Dienstordner. Physischer Testdruck und Windows-Neustart auf KDOF1 bleiben eine Vor-Ort-Abnahme.
