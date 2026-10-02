# Datenschutzerklärung von Mockingbird Studio

Gültig ab: 1. Oktober 2026

## Lokale Verarbeitung
Mockingbird Studio transkribiert Aufnahmen und korrigiert Transkripte auf Ihrem Computer. Die Anwendung lädt weder Medien noch Transkripte zu einem Transkriptionsdienst hoch. Sie hat keine eingebaute Analyse, kein Kontosystem, keine Werbung und keinen automatischen Upload von Absturzberichten.

## Auf Ihrem Computer gespeicherte Dateien
Projekte können den Pfad und den Hash der Quellmedien, normalisiertes Audio, Rohausgaben der Engines, Transkripte, bei der Überprüfung von Ihnen vorgenommene Änderungen, Zwischenstände, Hardware-Messungen und Anwendungsprotokolle enthalten. Modelle, Teildownloads, Aufzeichnungen über Prüfsummenprüfungen und Einstellungen werden lokal gespeichert. Diese Dateien können persönliche Informationen aus Ihren Aufnahmen oder Dateipfaden enthalten. Sie werden von der Anwendung nicht verschlüsselt.

Mit dem Installationspaket installierte Versionen verwenden das bisherige Verzeichnis LocalAppData/TriASR, sofern Sie keinen anderen Projekt- oder Modellordner wählen. So bleiben bestehende Installationen erhalten. Beim Deinstallieren der App bleiben Projekte, Einstellungen und heruntergeladene Modelle an ihrem Platz. Um Ihre Daten zu entfernen, schließen Sie die App und löschen Sie die gewählten Ordner selbst. Bewahren Sie Aufnahmen oder Exporte, die Sie behalten möchten, vorher auf.

## Netzwerkverbindungen
Wenn Sie Modell- oder Laufzeit-Downloads anfordern, verbindet sich die App mit Hugging Face oder GitHub und deren Download-Infrastruktur. Diese Anbieter erhalten übliche Verbindungsinformationen wie Ihre IP-Adresse und die angeforderte Datei. Medien- und Transkriptinhalte sind in diesen Download-Anfragen nicht enthalten. Für die Dienste der Anbieter gelten deren Datenschutzerklärungen:
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Update-Prüfungen
Sofern Sie dies nicht in den Einstellungen ausschalten, fragt die App beim Start, höchstens alle 12 Stunden, sowie immer dann, wenn Sie „Jetzt nach Updates suchen“ wählen, bei GitHub eine kleine Versionsdatei (latest.json) ab. GitHub erhält übliche Verbindungsinformationen wie Ihre IP-Adresse, und die Anfrage enthält den Namen und die Version der App. Es werden keine Aufnahmen, Transkripte, Projektdaten, Hardware-Details oder Kennungen gesendet. Die App installiert ein Update nie von selbst: Sie wählen „Jetzt aktualisieren“, und das heruntergeladene Installationsprogramm wird vor der Ausführung gegen seine veröffentlichte SHA256-Prüfsumme geprüft.

Der lokale Korrekturserver kommuniziert über die Loopback-Schnittstelle (127.0.0.1); dabei handelt es sich nicht um Cloud-Verarbeitung. Auch das Herunterladen des Installationsprogramms der App verbindet sich mit GitHub. Sobald die benötigten Modelle und Laufzeitumgebungen installiert sind, kann die Transkription offline arbeiten.

## Terminal, Exporte und Support
Befehle, die Sie im PowerShell-Bereich eingeben, haben die Berechtigungen Ihres Windows-Benutzers. Sie können je nach den ausgeführten Befehlen auf Dateien zugreifen, Netzwerkdienste kontaktieren oder Daten übertragen. Die Aussage zur lokalen Verarbeitung in dieser Erklärung beschreibt die Transkriptionsfunktionen der App, nicht beliebige Terminalbefehle.

Die App übermittelt Protokolle nicht automatisch. Sehen Sie Diagnosedaten, Engine-Ausgaben und Transkripte durch, bevor Sie sie in GitHub-Issues teilen. Das Kopieren oder Exportieren von Daten geht von Ihnen aus. Ihr Betriebssystem, Backups, synchronisierte Ordner, Sicherheitssoftware und Laufzeitumgebungen von Drittanbietern können Dateien nach ihren eigenen Richtlinien behandeln.

## Änderungen und Fragen
Künftige Versionen können diese Erklärung überarbeiten. Lesen Sie die Erklärung, die der von Ihnen installierten Version beiliegt. Fragen zum Datenschutz können über die GitHub-Issues des Projekts gestellt werden, ohne private Aufnahmen, Transkripte oder Protokolle anzuhängen.
