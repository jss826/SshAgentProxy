$ErrorActionPreference = "Stop"

$Repo = "jss826/SshAgentProxy"
$InstallDir = if ($env:SSH_AGENT_PROXY_INSTALL_DIR) { $env:SSH_AGENT_PROXY_INSTALL_DIR } else { "$env:LOCALAPPDATA\SshAgentProxy" }

# Fetch releases
Write-Host "Fetching releases..."
$Releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=10"

if (-not $Releases -or $Releases.Count -eq 0) {
    Write-Error "Failed to fetch releases."
    exit 1
}

# Display version menu
Write-Host ""
for ($i = 0; $i -lt $Releases.Count; $i++) {
    $r = $Releases[$i]
    $label = $r.tag_name
    if ($r.prerelease) { $label += " (pre-release)" }
    if ($i -eq 0) { $label += " *" }
    Write-Host "  [$i] $label"
}
Write-Host ""

# Read selection from console (works even in irm | iex)
Write-Host -NoNewline "Select version [0]: "
$Choice = [Console]::ReadLine()
if ([string]::IsNullOrWhiteSpace($Choice)) { $Choice = "0" }
$Index = [int]$Choice

if ($Index -lt 0 -or $Index -ge $Releases.Count) {
    Write-Error "Invalid selection: $Choice"
    exit 1
}

$Tag = $Releases[$Index].tag_name
$Asset = "SshAgentProxy-win-x64.zip"
$Url = "https://github.com/$Repo/releases/download/$Tag/$Asset"

Write-Host "Installing SshAgentProxy $Tag..."

# Download and extract
$TmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $TmpDir | Out-Null

try {
    $ZipPath = Join-Path $TmpDir $Asset
    Invoke-WebRequest -Uri $Url -OutFile $ZipPath -UseBasicParsing
    Expand-Archive -Path $ZipPath -DestinationPath $TmpDir -Force

    # Install
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Move-Item -Path (Join-Path $TmpDir "SshAgentProxy.exe") -Destination (Join-Path $InstallDir "SshAgentProxy.exe") -Force

    Write-Host "Installed SshAgentProxy $Tag to $InstallDir\SshAgentProxy.exe"
} finally {
    Remove-Item -Path $TmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Add to user PATH if not already present
$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -notlike "*$InstallDir*") {
    Write-Host -NoNewline "Add $InstallDir to your PATH? (Y/n): "
    $Answer = [Console]::ReadLine()
    if ($Answer -ne "n") {
        [Environment]::SetEnvironmentVariable("Path", "$UserPath;$InstallDir", "User")
        $env:Path = "$env:Path;$InstallDir"
        Write-Host "Added to PATH. Restart your terminal for it to take effect."
    } else {
        Write-Host "Skipped. Add it manually:`n  `$env:Path += `";$InstallDir`""
    }
}

# Set SSH_AUTH_SOCK if not configured
$CurrentSock = [Environment]::GetEnvironmentVariable("SSH_AUTH_SOCK", "User")
$ExpectedSock = "\\.\pipe\ssh-agent-proxy"
if ($CurrentSock -ne $ExpectedSock) {
    Write-Host -NoNewline "Set SSH_AUTH_SOCK to $ExpectedSock ? (Y/n): "
    $Answer = [Console]::ReadLine()
    if ($Answer -ne "n") {
        [Environment]::SetEnvironmentVariable("SSH_AUTH_SOCK", $ExpectedSock, "User")
        $env:SSH_AUTH_SOCK = $ExpectedSock
        Write-Host "SSH_AUTH_SOCK set. Restart your terminal for it to take effect."
    }
}

Write-Host ""
Write-Host "Done! Run 'SshAgentProxy' to start the proxy."
Write-Host "To start minimized: 'SshAgentProxy --minimized'"
