# Prüfung Agent ↔ Cloud vom 25.10.2026

Geprüfter Ausgangspunkt: Agent `df3a820` / v2026.10.23.1;
Cloud `74522acc`, Branch `feat/einsatzwissen-phasen1-3`, einschließlich des lokalen
Arbeitsstands. Bereits vorhandene fachfremde Änderungen im Cloud-Repository wurden
nicht bearbeitet. Es wurden keine Commits, Releases oder Deployments erstellt.
Ohne produktive Windows-Logs lässt sich keine konkrete Ausfallhistorie behaupten.

## Ursachen und Korrekturen

Fundstellen beziehen sich auf den korrigierten Arbeitsstand. Die erste Ursache
blockiert jeden betroffenen Auftrag deterministisch vor der Druckübergabe; die
weiteren Befunde betreffen Fehlerfälle und längere Aufträge.

| Ursache | Fundstelle und Änderung |
| --- | --- |
| **Intent-ID umgeht den Converter.** `SendIntentAsync` baute ein anonymes Objekt mit `string job_id`. Cloud `ws.py` akzeptierte ausschließlich `int`. Antwort: `ok=false`, `reason=unknown`; lokal anschließend `abandoned`, kein Submitter-Aufruf. | Agent `src/Einsatzcockpit.PrintAgent.Core/Cloud/CloudConnection.cs:90`: verwendet jetzt `LeaseReference` mit dem vorhandenen ID-Converter. Cloud `app/routers/ws.py:1383`: akzeptiert zusätzlich positive dezimale ASCII-String-IDs innerhalb des signed-BigInteger-Bereichs. Damit funktioniert auch der Intent installierter v2026.10.23.1-Agents. Token-, Org-, Eigentums-, Phasen- und Lease-Prüfungen bleiben im Service erhalten. |
| **Wartende Grants fehlen beim Renewal.** Bisher wartete die Runtime vor dem JobProcessor auf ihre beiden Druckplätze und auf die Queue-Sperre. Erst danach wurde ein Grant gespeichert. Währenddessen lief sein 60-s-Lease ohne Renewal ab. | Agent `src/Einsatzcockpit.PrintAgent.Core/Jobs/JobProcessor.cs:46`: speichert vor den unveränderten Parallelitätsgrenzen. Die Grenzen liegen dafür jetzt im Processor. `src/Einsatzcockpit.PrintAgent.Core/Runtime/AgentRuntime.cs:139` erfasst dadurch auch wartende Grants beim Renewal. |
| **Alter Download überschreibt neueren Zustand.** Nach `await FetchAsync` wurde der vorher gelesene Job ungeprüft als `intent_pending` zurückgeschrieben. Das konnte `abandoned` oder einen neueren Grant überschreiben. Auch ein alter Downloadfehler konnte einen neuen Grant als fehlgeschlagen markieren. | Agent `src/Einsatzcockpit.PrintAgent.Core/Jobs/JobProcessor.cs:69` und `:118`: lädt den Zustand nach dem Download neu und prüft Token und Phase; Fehler dürfen nur den eigenen Lease ändern. Regressionen für Abandon, neue Generation und Fehler des alten Downloads. |
| **Ack-Verlust ohne Reconnect bleibt liegen.** Outbox-Versand fand nur bei einer neuen Statusmeldung oder beim Connect statt. Ohne weiteres Ereignis wurde eine unbeantwortete Endmeldung nicht wiederholt. | Agent `src/Einsatzcockpit.PrintAgent.Core/Cloud/CloudConnection.cs:204`: pumpt die persistierte Outbox auch nach Heartbeat/Ping. Ack entfernt weiterhin nur die betreffende Outbox-ID. |
| **HTML-Fehlerseite kann als PDF gedruckt werden.** Puppeteer-Navigation wirft bei HTTP 403/422 nicht automatisch. Bisher wurde nach dem Ready-Timeout auch die Fehlerseite zu PDF. | Agent `src/Einsatzcockpit.PrintAgent/Rendering/EdgeHtmlRenderer.cs:37`: prüft erfolgreichen HTTP-Status und `text/html`, bevor Ready-Signal und PDF-Ausgabe folgen. Navigation, Ready-Wartezeit und PDF-Ausgabe beachten die Cancellation. |
| **Download-Body ohne wirksames Zeitlimit.** Bei `ResponseHeadersRead` endet der Bereich von `HttpClient.Timeout` mit dem Empfang der Header. Ein danach hängender Body konnte die Verarbeitung blockieren. | Agent `src/Einsatzcockpit.PrintAgent/Fetching/HttpArtifactFetcher.cs:50`: eigenes 60-s-Zeitlimit einschließlich Body. Zusätzlich Vergleich mit `Content-Length`, sofern vorhanden, und Aufräumen der temporären Datei bei Fehlern. Relative URLs werden jetzt gegen die konfigurierte Cloud-Adresse aufgelöst (`:46`, Übergabe in `Worker/AgentWorker.cs`). |

Intent und fehlende Outbox-Wiederholung wurden zunächst durch neue rote Agent-Tests
reproduziert. Der vollständige Cloud-WebSocket-Ablauf war für numerische IDs grün,
für die String-Intent-ID zunächst rot; nach der Korrektur bestehen beide Varianten.

## Vergleich Nachricht für Nachricht

| Nachricht / Schnittstelle | Cloud-Vertrag und Agent-Befund |
| --- | --- |
| Pairing | `POST /api/v1/gateway/pair`, JSON `code`; Antwort `device_token`, numerisches `gateway_id`, `name`. `PairingClient` verwendet diesen Pfad, Snake-Case und `long GatewayId`. Kein neuer Pairing- oder Token-HTTP-Vertrag seit dem Release gefunden. UI-Endpunkte erzeugen Codes bzw. rotieren/widerrufen Tokens, der Agent ruft diese nicht selbst auf. |
| WS/Auth | `/ws/print-agent`, `Authorization: Bearer <device_token>`; Cloud akzeptiert zusätzlich `?token=`, ausschließlich `windows_agent`. `AgentConfiguration.WebSocketUri` und `ClientWebSocketTransport` passen dazu. |
| Connection-ID | Cloud beansprucht die ID serverseitig beim Connect und prüft sie bei Nachrichten/Disconnect. Sie ist kein Feld des Agent-Handshakes. Die ID in lokalen Agent-Logs ist eine unabhängige lokale Diagnose-ID. |
| `hello` | Version, Host und `open_jobs`; deren IDs werden durch `OpenJob` numerisch serialisiert. Bei jedem Reconnect sendet die Runtime Hello und Inventar erneut. Cloud antwortet mit Konfiguration und gegebenenfalls `abandon`. |
| `config_sync` | Numerische `agent_id`, Route-/Printer-IDs, Optionen, Defaults, Identity-Check und Intervalle. Die ID-Converter bzw. `long`-Felder passen. Zusätzliche Konfigurationsfelder werden ignoriert. Erst gültiges `config_sync` setzt den lokalen Online-Nachweis. Vollständiges JSON-Beispiel aus `agent_config_sync` ergänzt. |
| `heartbeat`, `ping`, `pong` | Cloud: 30-s-Heartbeat, Presence-TTL 90 s, Auth-/Eigentumsprüfung auch bei 30 s Leerlauf. Hello/Heartbeat verlängern Presence, Inventar/Readiness nicht. Agent sendet Heartbeat plus Ping; Pong hält den 75-s-Empfangs-Timeout offen. Dafür ist keine neue Wire-Nachricht nötig. |
| `inventory`, `readiness` | Queue-Liste bzw. numerische `route_id`, Ready-State, Identity und optionale Capabilities stimmen überein. |
| `job_available` | Numerische `job_ids` werden gelesen; das Ereignis stößt Lease-Polling an. Periodisches Polling bleibt als Absicherung gegen verlorene Hinweise bestehen. |
| `lease_request`, `lease_grant` | `max`, Antwort `jobs` mit demselben `rid`; numerische Job-ID, Token, Generation, TTL, Dokumenttyp, URL, Render-Art, Druckoptionen und Route. Dokumentpaket-JSON wurde direkt mit dem aktuellen `_grant` und synthetischen Testdaten erzeugt. Nur URL-Signatur/Token sind Testwerte. |
| `lease_renew` | Numerische IDs über `LeaseReference`, unveränderte Tokens. Wartende Grants sind jetzt gespeichert und werden mit erneuert. |
| Artefakt-HTTP | `artifact_url()` liefert normalerweise absolute URLs unter `/api/v1/print/artifacts/{id}?sig=…` oder `/api/v1/print/render/{id}?sig=…`. Die Signatur authentifiziert den Download; kein Device-Token nötig. PDF-Download folgt HTTP-Redirects, prüft Erfolg, `application/pdf`, Länge sofern angegeben und `%PDF`-Header. Relative URLs werden ebenfalls unterstützt. |
| `print_intent`, `intent_result` | Numerische ID jetzt auch beim tatsächlichen Versand; Antwort über `rid` korreliert. Cloud lässt für v2026.10.23.1 zusätzlich die bisherige String-ID zu. Falscher Lease-Token wird in beiden Varianten weiterhin mit `not_owner` abgewiesen. |
| `job_status`, `ack` | Numerische Job-ID, String-Token und String-Spool-Referenz. `printing/spooled` erst nach Intent und Submitter. Fehler vor Übergabe werden als `failed/fetching`, unsicherer Ausgang als `uncertain/submitting` gemeldet. Die Cloud bestätigt auch veraltete Meldungen mit `ack.payload.id`; die Outbox verwendet eine String-Nachrichten-ID. Keine vorgezogene `printing`-Meldung beim Download, da dies den Cloud-Intent ungültig machen würde. |
| `cancel_job`, `abandon` | Numerische Job-IDs werden gelesen. Vor Übergabe wird lokal verworfen; ein später abgeschlossener Download darf dies jetzt nicht rückgängig machen. Keine automatische erneute Übergabe ab `submitting`. |
| `log_event` | Level und Message entsprechen dem unveränderten Vertrag. |

## Dokumentpaket, Druckoptionen und Änderungen seit dem Release

Der verlangte `git log --since=2026-10-23 -- …` und die betroffenen Diffs wurden
gelesen:

- `95d3929f` / #715: DB-Presence, Verbindungs-Fencing, Tokenrotation/Widerruf und
  UI-Diagnose. Die neue Presence verlangt keine zusätzliche ID vom Agent.
- `f65fa60d`: `dokumentpaket`, gefilterte Job-Optionen und pro Grant ergänzte
  Routenoptionen; neuer Server-PDF-Renderer. Der manuelle Gateway-Endpunkt nimmt
  Dokumentpakete nicht direkt an, die Erstellung erfolgt über den Dokumentviewer.
- `14504bb0`: Änderungen an `/ws/lage`, keine Änderung am Print-Agent-Vertrag.
- `d23c8626`: zusätzliche einbettbare Dokumentrouten in Security-Headers, keine
  neue Authentifizierung für signierte Druckartefakte.

**Die neuen Optionsfelder sind kein Deserialisierungsbruch.**
`Protocol/Messages.cs:23` verwendet `PrintOptions` für `copies/duplex/color/media`
und `JsonElement?` für Routenoptionen. `orientation="auto"`, `scaling="fit"` und
das echte JSON-Boolean `collate=true` bleiben damit korrekt erhalten.

Der Treiberpfad liest `collate` als Boolean
(`Printing/DriverPrintSubmitter.cs:26`), richtet jede PDF-Seite anhand ihrer
Abmessungen aus (`:63`) und passt sie bei `scaling=fit` in den Druckbereich ein
(`:172`). Das Zielpapier stammt aus `job.options.media` (`:139`); automatische
Papierfach-/Formatumschaltung pro Originalseite ist damit nicht zugesichert.
Duplex `long/short` wird durch `PrintValueMappings` normalisiert.
`pdf_direct` überträgt sämtliche PDF-Bytes und PJL für Kopien, Duplex, Papier und
Farbe (`Printing/RawPdfSubmitter.cs:26`). Zusätzliche Routenoptionen werden dort
ignoriert, ohne sie als falschen JSON-Typ auszulesen. Automatische Skalierung,
Ausrichtung und Sortierung hängen bei RAW-PDF vom Drucker ab.

Die Cloud kontrolliert Quellfingerprints im Dokumentpaket und begrenzt die
Auswahl auf 150 MiB Quelldaten / 2000 Seiten. Im Grant und in der finalen
Artefakt-Antwort gibt es **keinen SHA-256-Wert des fertigen PDFs**. Der Agent kann
daher keinen vom Server vorgegebenen PDF-Digest vergleichen. Die Signatur ist
URL-Authentifizierung, kein Inhaltschecksum. Die PDF-Prüfung im Fetcher ist eine
Headerprüfung, kein vollständiger PDF-Parser. Es wurde kein neuer willkürlicher
Download-Größencap eingeführt. Grenzlast mit 150 MiB/2000 Seiten wurde nicht getestet.

## Tatsächlich ausgeführte Prüfungen

**74 ausgewählte Agent-Tests bestanden, 0 fehlgeschlagen, 0 übersprungen** im
abschließenden kombinierten Lauf. Gefilterte Klassen: `ProtocolConnectionTests`,
`RuntimeTests`, `CloudTests`, `JobProcessorTests`, `ArtifactFetcherTests`,
`PrintingHelpersTests`, `PairingTests`, `AgentConfigurationTests`, `HtmlRendererTests`.
Es wurde nicht die gesamte Suite gestartet.

Die Tests decken unter anderem numerische IDs beim tatsächlichen Intent-Versand,
Reconnect und Config-Sync, Ack-Wiederholung auf offener Verbindung, Renewal
wartender Grants, alte Downloads nach Abandon/neuer Generation sowie die
Weitergabe des echten Dokumentpaket-Grants bis zu einem **Test-Submitter** für
`driver` und `pdf_direct` ab. Die nativen Druckimplementierungen werden dadurch
nicht als ausgeführt behauptet.

Der Fetcher wurde mit einem per pypdf erzeugten PDF mit drei Seiten (A4 hoch,
A3 quer, A4 quer) auf bytegleichen Download geprüft. Ein echter lokaler TCP/HTTP-
Server prüft Redirect und signierte URL ohne Authorization-Header. Weitere Fälle:
403, falscher MIME-Typ, fehlende PDF-Signatur, falsche Content-Length und
Cancellation während des Bodys mit gelöschter temporärer Datei.

Die vier HTML-Fälle verwenden echtes lokal vorhandenes Chromium: erfolgreiches
HTML wird als PDF ausgegeben, 403/422 und JSON statt HTML werden abgewiesen.
Der erste direkte Browserstart scheiterte an der Linux-Sandbox und schreibgeschützter
Crash-Konfiguration. Der erfolgreiche Lauf verwendet ausschließlich für die Tests
einen `/tmp`-Wrapper mit `--no-sandbox`, `--disable-dev-shm-usage` und XDG-Verzeichnissen
unter `/tmp`. Die Browser-Startoptionen des Agents wurden dafür nicht geändert.
Ein Fehler im lokalen Testserver für spekulative leere Chromium-Verbindungen wurde
vor dem abschließenden grünen Lauf korrigiert.

Aufruf im Agent-Repository:

```bash
export PATH=$HOME/.dotnet:$PATH
export NUGET_PACKAGES=/tmp/ecpa-nuget-packages
export NUGET_HTTP_CACHE_PATH=/tmp/ecpa-nuget-http-cache
export ECPA_TEST_BROWSER=/tmp/ecpa-test-chromium
dotnet test tests/Einsatzcockpit.PrintAgent.Tests -c Release \
  -p:EnableWindowsTargeting=true --no-restore \
  --filter 'FullyQualifiedName~ProtocolConnectionTests|FullyQualifiedName~RuntimeTests|FullyQualifiedName~CloudTests|FullyQualifiedName~JobProcessorTests|FullyQualifiedName~ArtifactFetcherTests|FullyQualifiedName~PrintingHelpersTests|FullyQualifiedName~PairingTests|FullyQualifiedName~AgentConfigurationTests|FullyQualifiedName~HtmlRendererTests'
```

Für den Restore wurden ein schreibbarer NuGet-Cache unter `/tmp` und
`-p:RestoreFallbackFolders=/home/johannes/.nuget/packages -p:NuGetAudit=false`
verwendet; der erste Restore gegen den schreibgeschützten Standardcache schlug
fehl. Es wird kein erfolgreicher NuGet-Vulnerability-Audit behauptet.
Browser-Tests werden ohne `ECPA_TEST_BROWSER` ausdrücklich übersprungen.

**Windows-Solution-Build unter Linux bestanden, 0 Warnungen / 0 Fehler:**

```bash
dotnet build Einsatzcockpit.PrintAgent.slnx -c Release \
  -p:EnableWindowsTargeting=true \
  -p:RestoreFallbackFolders=/home/johannes/.nuget/packages -p:NuGetAudit=false
```

**149 gezielte Cloud-Tests bestanden** (bestehende Bibliotheks-/SQLAlchemy-Warnungen):

```bash
PYTEST_XDIST_WORKER=printagent20261025 .venv/bin/python -m pytest \
  tests/test_print_agent_ws.py tests/test_print_agent_service.py \
  tests/test_print_agent_presence.py tests/test_dokumentdruck.py \
  tests/test_gateway_pr1.py -q
```

Die eigene Worker-Kennung isoliert die Testdatenbank. Der Lauf umfasst die neuen
String-Intent-Regressionsfälle und bestehende Token-, Presence-, Lease-,
Mandantengrenzen-, PDF-/Dokumentpaket- und Pairing-Tests. `git diff --check` war
für die Änderungen beider Repositories erfolgreich. Kein Formatierer, kein
`--fix`, keine vollständige Python-Suite.

## Ohne echten Windows-PC und Drucker offen

- Windows-Dienststart, LocalSystem/DPAPI, Tray, Installation/Update und Neustart
  wurden hier nicht ausgeführt.
- PDFium-Rasterung und GDI/DEVMODE, reale Spooler-IDs, Druckerstatus, Papierfach,
  Duplex, Farbe, Kopien/Collate und Papier-/Ausrichtungsverhalten müssen am
  Windows-Druckziel geprüft werden. Insbesondere RAW-PDF-Funktionen sind
  geräteabhängig.
- Chromium unter Linux bestätigt den HTML-Ablauf; Microsoft Edge unter Windows,
  produktive Leaflet-/Tile-Dienste und Proxy-/TLS-Verhältnisse vor Ort sind damit
  nicht abgenommen.
- Kein produktiver Testdruck, Cloud-Deployment, Migration, Windows-Release oder
  Belastungstest wurde ausgeführt. Die produktive Ausfallhistorie ist unbekannt.
