# Multi-Worker, Phase 4: Windows-Druckagent

Basis main `afbf064`, einschließlich Intent-ID-, Renewal- und Download-Fixes aus PR #6.
Keine produktive Veröffentlichung oder automatische Installation im Rahmen dieser Änderung.

Lease-Polling, `job_available`, Prioritätsrouten, persistierter Intent vor Spooler-
Übergabe und Reconnect-Recovery bleiben erhalten. Die Cloud hält den verbindlichen
Lease-/Generationsstatus und die gefencete Connection-Presence.

Endstatus und Ergebnis-Outbox werden jetzt gemeinsam in einer SQLite-Transaktion
committet. Schlägt die Outbox-Anlage fehl, wird auch die Statusänderung zurückgerollt.
Ein alter Besitzer oder ein verspäteter nichtterminaler Zustand kann keinen bereits
abgeschlossenen lokalen Auftrag überschreiben. Die Outbox wird bis zum Cloud-ACK
wiederholt. Die bestehende PDF-Bereinigung erhält nun die Job-ID als Dedup-Tombstone.

Nach möglicher Ausgabe gibt es keinen automatischen Ersatzdruck: Ein gelöschter
Spoolerjob, ein Fehler-Exitcode eines bereits gestarteten Druckkommandos, eine
fehlende IPP-Job-ID und unklare IPP-Fehler werden `uncertain`. Nur eine tatsächliche
Vorab-Ablehnung liefert `not_submitted=true`; die neue Cloud benötigt diesen
expliziten Nachweis für einen sicheren Fehler nach Intent. Alte Clouds können den
zusätzlichen Parameter ignorieren. Neue Clouds behandeln fehlende Nachweise alter
Agenten konservativ als unklar.

Erfolgreiche Kommando- und Ordnerausgaben bleiben als bestehende Druckmodi erhalten.
Ihre Referenzen werden weiterhin als abgeschlossene externe Ausgabe erkannt.

Die native Spoolerabfrage unterscheidet Enumerationsfehler von einer leeren Queue
und betrachtet unbekannte Referenzen nicht als erfolgreichen Druck. Maßgeblich ist
[Microsofts Fehlervertrag von EnumJobs](https://learn.microsoft.com/en-us/windows/win32/printdocs/enumjobs).
`done` beschreibt den beobachteten Spoolerabschluss; eine tatsächlich vorgefundene
Papierausgabe benötigt weiterhin die Geräteabnahme.

88 .NET-Tests bestehen, einschließlich erzwungenem Outbox-Fehler mit vollständigem
Rollback und Tests für unklare Kommando-/IPP-Ergebnisse. Ein HTML-Browsertest wird
nur mit gesetztem `ECPA_TEST_BROWSER` ausgeführt und bleibt hier ausgenommen.
Der vollständige Windows-Cross-Build ist erfolgreich. Der Test-Restore hatte eine
NuGet-Metadatenwarnung wegen des schreibgeschützten HTTP-Caches; die Tests und der
Produkt-Build sind davon getrennt erfolgreich.

Für die physische Abnahme fehlen freigegebene Windows-Testdrucker. Vor Freigabe
unter 2/4 Cloud-Workern testen: zwei PCs mit demselben Netzwerkdrucker, Druckrouten-
Priorität, konkurrierende Grants, Neustart vor/nach Intent, Redis-Ausfall,
Spoolerverzögerung, Drucker offline sowie manuelle Auflösung und gezielter Nachdruck.
Job-ID, Generation, Agent, Spoolerreferenz und tatsächliche Blattanzahl abgleichen.

Rollback erst nach Anhalten neuer Auslöser, Drain beziehungsweise manueller Prüfung
unklarer Ausgaben und Sicherung von Cloud-DB sowie lokalem SQLite-Journal. Ein alter
Client darf nicht durch Löschen des Journals oder Zurücksetzen möglicher Übergaben
zu einem vermeintlichen neuen Druckversuch gebracht werden.

## Nachprüfung: Recovery bei defekter Queue

Die Spoolersuche isoliert Exceptions je lokalem `submitting`-Job. Für einen nicht
abklärbaren Job werden `uncertain` und die Status-Outbox atomar gespeichert;
andere Jobs werden weiterhergestellt. Anschließend kann der Agent seine Cloud-
Verbindung und Polling-/Renewal-Schleifen starten. Ungültige gespeicherte Routen
werden ebenfalls je Job behandelt. Eine angeforderte Cancellation wird weitergereicht;
ein Persistenzfehler darf den sicheren Recovery-Abschluss weiterhin verhindern.

Die lokale DB-Struktur und das Cloud-Protokoll ändern sich dafür nicht. Logs
enthalten nur Job-ID und Exception-Klassennamen, keine Exception-Nutzdaten.
Ein erneuter Neustart eines bereits als unklar gespeicherten Jobs erzeugt keine
zweite Status-Outboxmeldung und keine erneute Druckübergabe.

Die Suite besteht mit **95 Tests**, einschließlich der vier HTML-/HTTP-Renderfälle
mit einem lokalen Chromium. Auf dem Linux-Prüfhost wurde dafür ausschließlich ein
temporärer Browserwrapper verwendet; Produktcode und produktive Browserargumente
bleiben unverändert. Der Windows-Cross-Build besteht ebenfalls. Das ersetzt weder
echte Windows-Spoolerfehler noch physische Ausdrucke auf den Zielgeräten.
