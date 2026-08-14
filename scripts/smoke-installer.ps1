param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory
)

$ErrorActionPreference = "Stop"
$artifactDirectory = (Resolve-Path $ArtifactDirectory).Path
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$asset = "monad-windows-$architecture.zip"
$archive = Join-Path $artifactDirectory $asset

if (-not (Test-Path $archive -PathType Leaf)) {
    throw "$archive does not exist"
}

$installDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("monad-installer-smoke-" + [guid]::NewGuid())
$serverDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("monad-installer-server-" + [guid]::NewGuid())
$previousEnvironment = @{
    MONAD_DOWNLOAD_BASE = [Environment]::GetEnvironmentVariable("MONAD_DOWNLOAD_BASE", "Process")
    MONAD_INSTALL_DIR = [Environment]::GetEnvironmentVariable("MONAD_INSTALL_DIR", "Process")
    MONAD_NO_PATH_UPDATE = [Environment]::GetEnvironmentVariable("MONAD_NO_PATH_UPDATE", "Process")
}
$server = $null

try {
    New-Item -ItemType Directory -Path $serverDirectory | Out-Null
    $servedArchive = Join-Path $serverDirectory $asset
    Copy-Item $archive $servedArchive
    $checksum = (Get-FileHash -Algorithm SHA256 $servedArchive).Hash.ToLowerInvariant()
    Set-Content -Path (Join-Path $serverDirectory "SHA256SUMS") -Value "$checksum  $asset"

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        $python = Get-Command python3 -ErrorAction Stop
    }

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
    $listener.Stop()

    $server = Start-Process $python.Source -ArgumentList @(
        "-m", "http.server", "$port", "--bind", "127.0.0.1", "--directory", $serverDirectory
    ) -PassThru `
        -RedirectStandardOutput (Join-Path $serverDirectory "server.stdout.log") `
        -RedirectStandardError (Join-Path $serverDirectory "server.stderr.log")

    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            Invoke-WebRequest "http://127.0.0.1:$port/SHA256SUMS" -UseBasicParsing | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 100
        }
    }

    if (-not $ready) {
        throw "local installer test server did not start"
    }

    $env:MONAD_DOWNLOAD_BASE = "http://127.0.0.1:$port"
    $env:MONAD_INSTALL_DIR = $installDirectory
    $env:MONAD_NO_PATH_UPDATE = "1"

    & (Join-Path $PSScriptRoot "..\install.ps1") | Out-Null
    # A second pass exercises replacement of an existing executable.
    & (Join-Path $PSScriptRoot "..\install.ps1") | Out-Null
    & (Join-Path $installDirectory "monad.exe") version | Out-Null
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }

    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        $server.WaitForExit()
    }

    Remove-Item $installDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $serverDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
