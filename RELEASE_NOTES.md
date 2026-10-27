## Windows-Druckagent: Druckfehler und Statusklarheit (unveröffentlicht)

- Fehler vor der Druckübergabe melden ausdrücklich `not_submitted`; die Cloud kann sicher einen Ersatzdruck auslösen.
- Identische Spoolerzustände erzeugen keine wiederholten Statusmeldungen. Änderungen und behobene Fehler werden weiterhin gemeldet.
- Nach einem Neustart gelten verschwundene Spooleraufträge als unklar statt automatisch als gedruckt; bitte am Drucker prüfen.

## Windows-Druckagent 2026.10.26: sichere Statusmeldungen und Aufräumen

- Statuswechsel und zugehörige Cloud-Meldung werden atomar in der lokalen Outbox gespeichert; ein Absturz dazwischen kann keine Meldung mehr verlieren.
- Spoolerfehler werden konservativ behandelt: Ein abgebrochener oder unklarer Druck wird als „unklar“ gemeldet und nie automatisch nachgedruckt. Nur nachweislich nicht übergebene Aufträge gelten als fehlgeschlagen (`not_submitted`), damit die Cloud gefahrlos ersatzdrucken kann.
- Recovery beim Start isoliert Fehler je Auftrag; ein defekter Spooler blockiert weder Reconnect noch andere Aufträge.
- Das Löschen eines Spoolerauftrags wird dauerhaft gespeichert; auch nach einem Neustart des Agents wird er korrekt als „unklar“ statt „erledigt“ gemeldet.
- Abgeschlossene Aufträge werden nach 90 Tagen endgültig aus der lokalen Datenbank entfernt (PDF-Daten weiterhin nach 7 Tagen).

Kompatibel mit der aktuellen ECP-Cloud; Vorbereitung für den Multi-Worker-Betrieb.
