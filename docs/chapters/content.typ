= Zugänge

== Source Code

- #link("https://github.com/baernhaeckt/SeniorsInTheMiddle")
  - /backend : Enthält das Backend der Applikation.
  - /frontend : Enthält das Frontend der Applikation.
  - /Worker1 : TODO
  - /docs : Enthält die Dokumentation für die Jury
  - /pitch : Enthält die Slides für den Pitch sowie den Screencast

== Deployment

- Frontend: #link("https://seniorsinthemiddle-frontend.icymushroom-561b0fa4.northeurope.azurecontainerapps.io/")
- Backend: #link("https://seniorsinthemiddle-backend.icymushroom-561b0fa4.northeurope.azurecontainerapps.io/swagger")
- CI/CD Pipelines: #link("https://github.com/baernhaeckt/SeniorsInTheMiddle/actions")
- Deployments: #link("https://github.com/orgs/baernhaeckt/packages?repo_name=SeniorsInTheMiddle")

= Ausgangslage

Die klassische Menüplanung ist ein manueller, repetitiver Prozess: Nutzer müssen Vorlieben, Unverträglichkeiten, Ernährungsziele und Vorräte berücksichtigen. Das führt oft zu langen Planungszeiten, Lebensmittelverschwendung und ungenutzten Synergien zwischen Haushaltsteilnehmern. Bestehende digitale Lösungen beschränken sich meist auf Rezeptauswahl und das Erstellen von Einkaufslisten, ohne dabei den gesamten Prozess intelligent und interaktiv zu orchestrieren. Diese Ausgangslage ist abgeleitet von der Challenge Beschreibung (siehe Anhang 2).

= Lösungsansatz

Profile & Digital Twins
Jeder Benutzer erstellt ein Profil mit Hardfacts (Unverträglichkeiten, Gewohnheiten, Ziele wie Abnehmen oder Marathontraining). Daraus wird eine Persona generiert. Ein Digital Twin, der stellvertretend an der Menüplanung teilnimmt. Haushalte bestehen aus beliebig vielen Personas, ergänzt durch Berater-Agents wie z. B. einen Umweltschützer oder den Chef, der die Planung koordiniert.

Smart Fridge Integration & Triggering
Der smarte Kühlschrank erkennt Restbestände und löst den Planungsprozess aus. Bestehende Zutaten werden automatisch berücksichtigt, um Food Waste zu reduzieren.

Interaktives Geschmacks-Sampling (Swipe-Mechanismus)
Über ein Tinder-ähnliches Interface „swipen“ Nutzer Karten mit automatisch generierten Menü-Bildern (je 3 Menüs pro Karte). Akzeptierte Menüs liefern nicht Menüs direkt, sondern Zutatenpräferenzen. Daraus generiert das System einen Pool von 20 Menü-Kandidaten.

Multi-Agenten-Diskussion (LLM-powered)
Die Persona-Agents (inkl. Berater und Chef) diskutieren die Vorschläge in einem simulierten Chat, live sichtbar für die Benutzer. Dabei werden Intoleranzen, Vorlieben und individuelle Ziele verhandelt. Reale Haushaltsmitglieder können sich in Echtzeit einklinken.

Finalisierung & Einkauf
Am Ende der Agentendiskussion steht ein Wochenplan. Daraus wird automatisch eine produktgenaue Einkaufsliste erstellt optimiert auf den ausgewählten Detailhändler (z. B. Coop oder Migros).

= Implementierung

- Menü- und Recommender-System trainiert auf grossen Open-Source-Datasets.
- Ingredients-first Matching: System erkennt bevorzugte Zutaten statt nur Rezepte.
- Multi-Agent Simulation mit LLMs für kollaborative Entscheidungsfindung.
- API-Integrationen für Händler-Produktkataloge und smarte Haushaltsgeräte.
- Secure: Secrets und config

= Technischer Aufbau

== Bausteinsicht

Aalla @bausteinsicht zeigt blablbala..

#figure(
  image("/assets/bausteinsicht.svg"),
  caption: [
    Die strukturelle Ansicht des Software Systems.
  ],
) <bausteinsicht>

== Laufzeitsicht

Aalla @laufzeitsicht zeigt blablbala..

#figure(
  image("/assets/laufzeitsicht.svg"),
  caption: [
    Das Software System zur Laufzeit.
  ],
) <laufzeitsicht>

== Verteilungssicht

Die @verteilsicht zeigt blablbala..


#figure(
  image("/assets/verteilungssicht.svg"),
  caption: [
    Das Software System installiert auf der Produktion.
  ],
) <verteilsicht>

== Technologien und Frameworks

= Abgrenzung / Offene Punkte

= Literatur

= Anhang
