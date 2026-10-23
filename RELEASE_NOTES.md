## Windows-Druckagent: Installation und Pairing korrigiert

- PowerShell-Parameter eindeutig benannt: `-PairingCode`, Alias `-Code`;
  Positionsargumente koennen niemals als `ServicePassword` gebunden werden.
- `Installieren.cmd` als Administrator starten und Code verdeckt eingeben.
- Pairing-Antwort der ECP-Cloud (`device_token`, numerische `gateway_id`) korrekt lesen.
- Code nicht in Kindprozessargumenten, Windows-Passwort nur bei ausdruecklichem Dienstkonto.
- Voraussetzungen und Windows-Dienstbefehle pruefen; frische Cloud-Verbindung nach Start abwarten.
- Updates und Neuinstallation erhalten Konfiguration, Token, Auftraege und vorhandenes Dienstkonto.
- Bestehende Druckprotokolle, Lease/Intent, Autostart und automatische Updates bleiben erhalten.

Dieses neue Paket ersetzt das fehlerhafte `2026.10.17-win-x64` fuer Erstinstallationen.
Bereits verbundene Agents muessen nicht neu gekoppelt werden.

ZIP vollstaendig entpacken und `Installieren.cmd` per Rechtsklick als Administrator starten.
Alternativ in Administrator-PowerShell:

```powershell
.\install.ps1 -CloudUrl "https://einsatzcockpit.com" -PairingCode "<PAIRING-CODE>"
```

Ohne `-PairingCode` erfolgt eine verdeckte Abfrage. Nur einen Bindestrich verwenden.
