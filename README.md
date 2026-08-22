# SeniorsInTheMiddle

Ein Man-in-the-Middle-Proxy als Grenzposten zwischen den Geräten einer Organisation
und fremden Cloud-Diensten. Der Proxy terminiert auch verschlüsselte Verbindungen
(CONNECT-Tunnel, eigenes CA-Zertifikat), liest nur Bodies, die überhaupt Personendaten
tragen können, erkennt darin Namen, Adressen, AHV-Nummern, IBANs und Ähnliches und
ersetzt sie formattreu, bevor die Anfrage die eigene Grenze verlässt. Die Tabelle von
Token zu echtem Wert bleibt im Proxy. Ein Dashboard zeigt jeden Schritt live mit.

Ausführliche Beschreibung: `docs/` (Jury-Dokumentation).

## Source Code

- `/backend` : Forward-Proxy, WebAPI und Telemetry-Stream als ein Prozess
  (`backend/src/SeniorsInTheMiddle.Proxy`). Das Image (`backend/Dockerfile`,
  Build-Context ist das Repo-Root) enthält zusätzlich die Python-Services aus
  `/services` als Daemons unter supervisord.
- `/services` : Python-Services und die gemeinsame Unix-Socket-Runtime, die im
  Backend-Container neben dem Proxy laufen. `pii_service` (Presidio/spaCy) und
  `privacy_check_service` (Re-Identifikationsrisiko, sentence-transformers/pymc)
  sind im Image verdrahtet. Siehe
  [services/README.md](services/README.md).
- `/frontend` : Dashboard (React/Vite), eigenes schlankes nginx-Image. Siehe
  [frontend/README.md](frontend/README.md).
- `/integration` : Testumgebung, die das Backend-Image unverändert betreibt und
  Verkehr durchschickt (Sender, Receiver, Test-UI). Enthält zusätzlich den
  Demo-Browser (`integration/ProxiedBrowser`, .NET/Avalonia/CEF), der den Proxy und
  dessen CA nur im eigenen Prozess konfiguriert. Siehe
  [integration/README.md](integration/README.md).
- `/notebooks` : Explorative Notebooks zur Erkennungsqualität.
- `/docs` : Dokumentation für die Jury (Typst, wird per Pipeline gebaut).
- `/pitch` : Slides für den Pitch sowie der Screencast.

## Lokal starten

Alles zusammen, so wie es deployed wird:

```bash
cd integration && cp .env.example .env && docker compose up --build
```

Test-UI auf http://localhost:3100, Proxy auf 3128, API auf 8080. Das Dashboard
kommt mit `docker compose --profile dashboard up` dazu (http://localhost:8081).

Einzeln während der Entwicklung:

```bash
dotnet run --project backend/src/SeniorsInTheMiddle.Proxy
```

```bash
cd frontend && npm install && npm run dev
```

Auf Windows gibt es keine Unix-Sockets: `Services__Pii__SocketPath` und
`Services__PrivacyCheck__SocketPath` bleiben leer, der Proxy läuft dann ohne
PII-Erkennung und Risiko-Check. Für den vollen Pfad das Backend-Image bauen
(`docker build -f backend/Dockerfile -t sitm-proxy .`, Build-Context ist das Repo-Root).

## Deployment

- Frontend: https://seniorsinthemiddle-frontend.icymushroom-561b0fa4.northeurope.azurecontainerapps.io
- Backend: https://seniorsinthemiddle-backend.icymushroom-561b0fa4.northeurope.azurecontainerapps.io/swagger
  - Proxy-CA: `/ca.crt` — PAC: `/proxy.pac`
- CI/CD Pipelines: https://github.com/baernhaeckt/SeniorsInTheMiddle/actions
- Deployments: https://github.com/orgs/baernhaeckt/packages?repo_name=SeniorsInTheMiddle

Frontend und Backend werden getrennt gebaut und deployed. Das Frontend ruft das Backend
cross-origin auf; die erlaubten Origins stehen in `Cors:AllowedOrigins`. Der Login der
öffentlichen Demo wird beim Start neu angelegt (`Auth:SeedUser`, `demo`/`demo`) und ist
bewusst öffentlich: dahinter liegt ausschliesslich synthetischer Verkehr.
