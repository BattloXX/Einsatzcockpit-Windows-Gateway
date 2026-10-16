Erste Version des nativen Windows-Druckagents für Einsatzcockpit, als .NET-10-Dienst ohne Docker.

**Voraussetzung:** Einsatzcockpit mit den Pull Requests #591 und #592 (Migration 0289).

## Funktionen
- Windows-Dienst `EinsatzcockpitPrintAgent`:
  - startet automatisch (verzögert), auch ohne Anmeldung
  - startet nach einem Absturz von selbst neu
- Nur ausgehende WSS-Verbindung; jeder PC hat ein eigenes Pairing-Token, das mit DPAPI geschützt gespeichert wird.
- Redundanz: Mehrere PCs bedienen ein Druckziel (z. B. Kommandofunk) mit Priorität und automatischem Ausweichen.
- Sichere Druckübergabe:
  - gedruckt wird nur nach Freigabe durch die Cloud
  - bei einem Neustart mitten im Druck wird der Auftrag als „Unklar – bitte prüfen“ gemeldet statt doppelt gedruckt
- Druckwege:
  - Windows-Druckertreiber
  - PDF direkt (RAW + PJL)
  - IPP direkt
  - Ordnerablage
  - externes Programm (nur über eine lokale Allowlist freigegeben)
- Kartenausdrucke werden mit dem vorhandenen Microsoft Edge gerendert.
- Inventar, Druckerfähigkeiten, Bereitschafts- und Identitätsprüfung werden an die Cloud gemeldet.

## Installation
ZIP entpacken und PowerShell **als Administrator** starten:

```powershell
.\install.ps1 -CloudUrl https://<einsatzcockpit> -PairingCode <CODE>
```

Den Pairing-Code erzeugt man in Einsatzcockpit unter *Gateway / Druck → Gateway anlegen (Typ: Windows-Druckagent)*. Details stehen in der README.
