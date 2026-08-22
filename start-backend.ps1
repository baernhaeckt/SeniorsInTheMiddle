#Requires -Version 5.1
<#
.SYNOPSIS
  Builds backend/Dockerfile and runs the proxy locally so the frontend and
  ProxiedBrowser can talk to it over localhost while browsing public URLs.

.DESCRIPTION
  This is not the integration harness (sender/receiver). It is the production
  image, started on this machine:

    3128  proxy traffic, /ca.crt, /proxy.pac
    3127  the same proxy inside TLS
    8080  WebAPI, Swagger, telemetry hub

  Point ProxiedBrowser at localhost:3128 and open any public site; the dashboard
  at http://localhost:5173 (npm run dev in frontend/) reads the live hub.

  Do not run `docker compose up` in integration/ at the same time — those
  containers bind the same ports.

  The MITM CA is kept in the named volume sitm-ca so clients do not have to
  re-trust a new root every time the container is recreated.

.PARAMETER NoBuild
  Start the already-built image without calling docker build.

.PARAMETER Rebuild
  docker build --no-cache. Implies a build; do not combine with -NoBuild.

.PARAMETER Stop
  Stop and remove the container, then exit. The CA volume is left in place.

.PARAMETER ResetCa
  Delete the sitm-ca volume so the next start mints a new CA. ProxiedBrowser
  then has to download it again (restart the app). The running container is
  stopped first because the volume is in use. Combined with -Stop, the
  container stays down; otherwise it is started again.

.PARAMETER Follow
  Tail container logs after it is up.

.PARAMETER JwtKey
  Jwt:Key. Defaults to JWT_KEY, then to the same throwaway the harness uses.
  The app will not start without one.

.PARAMETER LogLevel
  Logging__LogLevel__Default. Information already logs decrypted tunnel chunks.
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $Rebuild,
    [switch] $Stop,
    [switch] $ResetCa,
    [switch] $Follow,
    [string] $JwtKey,
    [string] $LogLevel = 'Information'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$image = 'sitm-proxy:local'
$container = 'sitm-proxy'
$volume = 'sitm-ca'
$repoRoot = $PSScriptRoot
$defaultJwt = 'aGFybmVzcy1vbmx5LWtleS1ub3QtYS1zZWNyZXQtMDAwMQ=='
$allowedOrigins = @(
    'http://localhost:5173',
    'http://127.0.0.1:5173',
    'http://localhost:8080',
    'http://127.0.0.1:8080'
)

if ($NoBuild -and $Rebuild) {
    throw '-NoBuild and -Rebuild cannot be used together.'
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'docker is not on PATH. Install Docker Desktop and make sure it is running.'
}

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]] $DockerArgs
    )
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($DockerArgs -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-ContainerExists {
    $null = & docker inspect -f '{{.Id}}' $container 2>$null
    return $LASTEXITCODE -eq 0
}

function Stop-ProxyContainer {
    if (-not (Test-ContainerExists)) {
        return
    }
    Write-Host "Stopping $container..."
    Invoke-Docker stop $container
    Invoke-Docker rm $container
}

function Wait-Http {
    param(
        [Parameter(Mandatory = $true)] [string] $Url,
        [int] $TimeoutSec = 120,
        [int[]] $Ok = @(200, 204)
    )
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSec)
    $lastError = 'no response yet'
    while ([datetime]::UtcNow -lt $deadline) {
        $status = & docker inspect -f '{{.State.Status}}' $container 2>$null
        if ($LASTEXITCODE -ne 0 -or $status -ne 'running') {
            Write-Host ''
            & docker logs $container
            throw "Container $container is not running (status: $status). See logs above."
        }
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($Ok -contains [int]$response.StatusCode) {
                return
            }
            $lastError = "HTTP $($response.StatusCode)"
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Seconds 2
    }
    Write-Host ''
    & docker logs --tail 80 $container
    throw "Timed out after ${TimeoutSec}s waiting for $Url ($lastError). See logs above."
}

if ($Stop -or $ResetCa) {
    Stop-ProxyContainer
}

if ($ResetCa) {
    $null = & docker volume inspect -f '{{.Name}}' $volume 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Removing volume $volume (a new CA will be minted on the next start)..."
        Invoke-Docker volume rm $volume
    }
    else {
        Write-Host "Volume $volume does not exist."
    }
}

if ($Stop) {
    return
}

if (-not $JwtKey) {
    if ($env:JWT_KEY) {
        $JwtKey = $env:JWT_KEY
    }
    else {
        $JwtKey = $defaultJwt
    }
}

if (-not $NoBuild) {
    $buildArgs = @('build')
    if ($Rebuild) {
        $buildArgs += '--no-cache'
    }
    $buildArgs += @('-f', 'backend/Dockerfile', '-t', $image, $repoRoot)
    Write-Host "Building $image (first run downloads spaCy models; later runs are cached)..."
    Invoke-Docker @buildArgs
}

Stop-ProxyContainer

$runArgs = @(
    'run', '-d',
    '--name', $container,
    '--restart', 'unless-stopped',
    '-p', '3128:3128',
    '-p', '3127:3127',
    '-p', '8080:8080',
    '-v', "${volume}:/app/certs",
    '-e', "Jwt__Key=$JwtKey",
    '-e', 'Proxy__HttpPort=3128',
    '-e', 'Proxy__HttpsPort=3127',
    '-e', 'Proxy__ApiPort=8080',
    '-e', 'Proxy__HostNames__0=localhost',
    '-e', "Logging__LogLevel__Default=$LogLevel",
    '-e', 'Proxy__Name=Seniors in the Middle (local)'
)
for ($i = 0; $i -lt $allowedOrigins.Count; $i++) {
    $runArgs += '-e'
    $runArgs += "Cors__AllowedOrigins__$i=$($allowedOrigins[$i])"
}
$runArgs += $image

Write-Host "Starting $container..."
Invoke-Docker @runArgs

Write-Host 'Waiting for the API...'
Wait-Http -Url 'http://localhost:8080/health' -TimeoutSec 90

Write-Host 'Waiting for the proxy CA...'
Wait-Http -Url 'http://localhost:3128/ca.crt' -TimeoutSec 60

Write-Host 'Waiting for the python services (spaCy / embedding model load)...'
Wait-Http -Url 'http://localhost:8080/healthz' -TimeoutSec 180

Write-Host @'

Backend is up.

  API / Swagger     http://localhost:8080/swagger
  Telemetry hub     http://localhost:8080/hub/telemetry
  Proxy (HTTP)      localhost:3128
  Proxy (TLS)       localhost:3127
  CA                http://localhost:3128/ca.crt
  PAC               http://localhost:3128/proxy.pac

Frontend — from frontend/:  npm run dev
  Open http://localhost:5173 and keep the setup defaults (live proxy, hub and
  host above). Origin http://localhost:5173 is on the CORS allow-list.

ProxiedBrowser — settings (restart the app after saving):
  UseProxy     true
  ProxyScheme  http
  ProxyHost    localhost
  ProxyPort    3128
  CaCertUrl    http://localhost:3128/ca.crt
  StartPage    https://example.com     (any public URL)

Logs:  docker logs -f sitm-proxy
Stop:  .\start-backend.ps1 -Stop

'@

if ($Follow) {
    & docker logs -f $container
}
