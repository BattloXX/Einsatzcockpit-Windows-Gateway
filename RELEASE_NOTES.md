## Neu in dieser Version
- **Tray-Symbol** für jeden angemeldeten Benutzer, startet automatisch:
  - Ampelfarbe grün, gelb oder rot für Verbindung und Bereitschaft, mit Hinweisen bei Verbindungsverlust, unklaren Aufträgen und Updates
  - **Statusfenster** mit Druckzielen, Aufträgen und Update-Stand
  - **Protokoll** mit Filter (Warnungen/Fehler), Suche und automatischem Mitlaufen
  - **Hilfe** zu Kopplung, Druckereinrichtung, Redundanz, „Unklar – bitte prüfen“, Updates und Fehlersuche
- **Automatische Updates** aus GitHub-Releases:
  - Prüfung alle 6 Stunden oder auf Knopfdruck im Tray, mit SHA256-Prüfung
  - während eines Updates nimmt der Agent keine Aufträge an, ein redundanter PC übernimmt
  - Installation nur ohne laufende Druckübergabe
  - Gesundheitscheck nach dem Neustart, bei Fehlschlag automatischer Rollback; die Version wird dann gesperrt
  - in `agent.json` abschaltbar mit `"auto_update": false`

**Hinweis:** Auf PCs mit Version 2026.10.16 dieses Paket einmal mit `install.ps1` installieren. Erst ab dieser Version aktualisiert sich der Agent selbst.

Installation: ZIP entpacken und PowerShell **als Administrator** starten:

```powershell
.\install.ps1 -CloudUrl https://<einsatzcockpit> -PairingCode <CODE>
```
