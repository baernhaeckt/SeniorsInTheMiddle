# SeniorsInTheMiddle

## Source Code

- /backend : Enthält das Backend der Applikation. Forward-Proxy, WebAPI und der
  Telemetry-Stream laufen als ein Prozess (`backend/src/SeniorsInTheMiddle.Proxy`).
- /frontend : Enthält das Frontend der Applikation. Eigenes, schlankes nginx-Image.
- /Worker1 : TODO
- /docs : Enthält die Dokumentation für die Jury
- /pitch : Enthält die Slides für den Pitch sowie den Screencast

## Deployment

- Frontend: https://seniorsinthemiddle-frontend.greensea-158b1300.northeurope.azurecontainerapps.io
- Backend: https://seniorsinthemiddle-backend.greensea-158b1300.northeurope.azurecontainerapps.io/swagger
  - Proxy-CA: `/ca.crt` — PAC: `/proxy.pac`
- CI/CD Pipelines: https://github.com/baernhaeckt/SeniorsInTheMiddle/actions
- Deployments: https://github.com/orgs/baernhaeckt/packages?repo_name=SeniorsInTheMiddle

Frontend und Backend werden getrennt gebaut und deployed. Das Frontend ruft das Backend
cross-origin auf; die erlaubten Origins stehen in `Cors:AllowedOrigins`.
