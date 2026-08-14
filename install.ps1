param(
    [string] $Version = $env:MONAD_VERSION,
    [string] $InstallDir = $env:MONAD_INSTALL_DIR
)

$ErrorActionPreference = "Stop"
$previousProgressPreference = $ProgressPreference
$ProgressPreference = "SilentlyContinue"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "latest"
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\Monad\bin"
}

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

switch ($architecture) {
    "X64" { $assetArchitecture = "x64" }
    "Arm64" { $assetArchitecture = "arm64" }
    default { throw "monad installer: unsupported Windows architecture: $architecture" }
}

$repository = if ([string]::IsNullOrWhiteSpace($env:MONAD_REPOSITORY)) {
    "leduftw/monad"
} else {
    $env:MONAD_REPOSITORY
}

if (-not [string]::IsNullOrWhiteSpace($env:MONAD_DOWNLOAD_BASE)) {
    $downloadBase = $env:MONAD_DOWNLOAD_BASE.TrimEnd("/")
} elseif ($Version -eq "latest") {
    $downloadBase = "https://github.com/$repository/releases/latest/download"
} else {
    $normalizedVersion = $Version.TrimStart("v")
    $downloadBase = "https://github.com/$repository/releases/download/v$normalizedVersion"
}

$asset = "monad-windows-$assetArchitecture.zip"
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("monad-install-" + [guid]::NewGuid())
$archive = Join-Path $workDir $asset
$checksums = Join-Path $workDir "SHA256SUMS"
$payload = Join-Path $workDir "payload"
$stagedExecutable = $null

try {
    New-Item -ItemType Directory -Path $workDir | Out-Null

    Write-Host "Downloading $asset..."
    Invoke-WebRequest "$downloadBase/$asset" -OutFile $archive
    Invoke-WebRequest "$downloadBase/SHA256SUMS" -OutFile $checksums

    $escapedAsset = [regex]::Escape($asset)
    $checksumLine = Get-Content $checksums | Where-Object {
        $_ -match "^([0-9a-fA-F]{64})\s+\*?$escapedAsset$"
    } | Select-Object -First 1

    if ($null -eq $checksumLine) {
        throw "monad installer: SHA256SUMS has no entry for $asset"
    }

    $expected = ([regex]::Match($checksumLine, "^[0-9a-fA-F]{64}")).Value.ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()

    if ($actual -ne $expected) {
        throw "monad installer: checksum verification failed for $asset"
    }

    Expand-Archive -Path $archive -DestinationPath $payload -Force

    $source = Join-Path $payload "monad.exe"
    if (-not (Test-Path $source -PathType Leaf)) {
        throw "monad installer: $asset did not contain monad.exe"
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $destination = Join-Path $InstallDir "monad.exe"
    $stagedExecutable = Join-Path $InstallDir (".monad." + [guid]::NewGuid() + ".new.exe")
    Copy-Item $source $stagedExecutable
    $installedVersion = & $stagedExecutable version
    Move-Item $stagedExecutable $destination -Force
    $stagedExecutable = $null

    if ($env:MONAD_NO_PATH_UPDATE -ne "1") {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        $pathEntries = @($userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $alreadyOnPath = $pathEntries | Where-Object {
            [string]::Equals($_.TrimEnd("\"), $InstallDir.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase)
        }

        if (-not $alreadyOnPath) {
            $newUserPath = (($pathEntries + $InstallDir) -join ";")
            [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
        }
    }

    if (-not (($env:Path -split ";") -contains $InstallDir)) {
        $env:Path = "$InstallDir;$env:Path"
    }

    Write-Host "Installed $installedVersion in $InstallDir"
    Write-Host "Set AUDD_API_TOKEN, then run: monad"
} finally {
    $ProgressPreference = $previousProgressPreference

    if (Test-Path $workDir) {
        Remove-Item $workDir -Recurse -Force
    }

    if ($null -ne $stagedExecutable -and (Test-Path $stagedExecutable)) {
        Remove-Item $stagedExecutable -Force
    }
}
