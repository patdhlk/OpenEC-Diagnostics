# OpenEC-Diagnostics CLI installer for Windows
# Usage: irm https://raw.githubusercontent.com/patdhlk/OpenEC-Diagnostics/main/install.ps1 | iex
# Or: .\install.ps1 [-Version "vX.Y.Z"]

param(
    [string]$Version = ""
)

$ErrorActionPreference = 'Stop'

$Repo = "patdhlk/OpenEC-Diagnostics"
$BinaryName = "openec"

function Write-Info {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

function Write-Error-And-Exit {
    param([string]$Message)
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Get-Architecture {
    $arch = $env:PROCESSOR_ARCHITECTURE
    switch ($arch) {
        "AMD64" { return "x64" }
        "ARM64" { return "arm64" }
        default { Write-Error-And-Exit "Unsupported architecture: $arch" }
    }
}

function Get-LatestVersion {
    Write-Info "Fetching latest release version..."
    
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest"
        return $release.tag_name
    }
    catch {
        Write-Error-And-Exit "Failed to fetch release information from GitHub API: $_"
    }
}

function Get-VersionInfo {
    param([string]$VersionArg)
    
    if ($VersionArg) {
        # Ensure tag has leading 'v'
        if ($VersionArg.StartsWith("v")) {
            $tag = $VersionArg
            $numericVersion = $VersionArg.Substring(1)
        }
        else {
            $tag = "v$VersionArg"
            $numericVersion = $VersionArg
        }
        Write-Info "Using specified version: $tag"
    }
    else {
        $tag = Get-LatestVersion
        # Strip leading 'v' for numeric version
        $numericVersion = $tag.TrimStart('v')
        Write-Info "Using latest version: $tag"
    }
    
    return @{
        Tag = $tag
        NumericVersion = $numericVersion
    }
}

function Test-Checksum {
    param(
        [string]$FilePath,
        [string]$ChecksumFile,
        [string]$AssetName
    )
    
    Write-Info "Verifying checksum..."
    
    # Read SHA256SUMS file and find the line for our asset
    $checksumContent = Get-Content $ChecksumFile
    $expectedLine = $checksumContent | Where-Object { $_ -match [regex]::Escape($AssetName) }
    
    if (-not $expectedLine) {
        Write-Error-And-Exit "Could not find checksum for $AssetName in SHA256SUMS"
    }
    
    # Extract the hash (first field, space/tab separated)
    $expectedHash = ($expectedLine -split '\s+')[0]
    
    # Compute actual hash
    $actualHash = (Get-FileHash -Path $FilePath -Algorithm SHA256).Hash
    
    if ($expectedHash -ne $actualHash) {
        Write-Error-And-Exit "Checksum verification failed!`nExpected: $expectedHash`nGot:      $actualHash"
    }
    
    Write-Info "Checksum verified successfully"
}

function Test-NpcapInstalled {
    $npcapPath = Join-Path $env:SystemRoot "System32\Npcap\wpcap.dll"
    return Test-Path $npcapPath
}

function Add-ToUserPath {
    param([string]$Directory)
    
    # Get current user PATH
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    
    # Check if directory is already in PATH
    $pathEntries = $userPath -split ';' | ForEach-Object { $_.TrimEnd('\') }
    $normalizedDir = $Directory.TrimEnd('\')
    
    if ($pathEntries -contains $normalizedDir) {
        Write-Info "Install directory is already in PATH"
        return $false
    }
    
    # Add to PATH
    if ($userPath) {
        $newPath = "$userPath;$Directory"
    }
    else {
        $newPath = $Directory
    }
    
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Write-Info "Added $Directory to user PATH"
    return $true
}

# Main installation flow
function Install-OpenEC {
    Write-Host ""
    Write-Info "OpenEC-Diagnostics CLI Installer"
    Write-Info "================================"
    Write-Host ""
    
    # Get version info
    $versionInfo = Get-VersionInfo -VersionArg $Version
    $tag = $versionInfo.Tag
    $numericVersion = $versionInfo.NumericVersion
    
    # Detect architecture
    $arch = Get-Architecture
    Write-Info "Detected architecture: win-$arch"
    
    # Construct asset name and URLs
    $assetName = "$BinaryName-$numericVersion-win-$arch.zip"
    $downloadUrl = "https://github.com/$Repo/releases/download/$tag/$assetName"
    $checksumUrl = "https://github.com/$Repo/releases/download/$tag/SHA256SUMS"
    
    Write-Info "Downloading $assetName..."
    
    # Create temporary directory
    $tempDir = Join-Path $env:TEMP "openec-install-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    
    try {
        # Download asset and checksums
        $assetPath = Join-Path $tempDir $assetName
        $checksumPath = Join-Path $tempDir "SHA256SUMS"
        
        Invoke-WebRequest -Uri $downloadUrl -OutFile $assetPath -UseBasicParsing
        Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing
        
        # Verify checksum
        Test-Checksum -FilePath $assetPath -ChecksumFile $checksumPath -AssetName $assetName
        
        # Determine install directory
        $installDir = Join-Path $env:LOCALAPPDATA "Programs\openec"
        Write-Info "Installing to $installDir..."
        
        # Create install directory if it doesn't exist
        if (-not (Test-Path $installDir)) {
            New-Item -ItemType Directory -Path $installDir -Force | Out-Null
        }
        
        # Extract zip (overwrite existing files)
        Write-Info "Extracting archive..."
        Expand-Archive -Path $assetPath -DestinationPath $installDir -Force
        
        # Add to PATH if needed
        $pathModified = Add-ToUserPath -Directory $installDir
        
        Write-Host ""
        Write-Host "✓ Installation successful!" -ForegroundColor Green
        Write-Host "  Binary installed to: $installDir\$BinaryName.exe" -ForegroundColor Green
        
        if ($pathModified) {
            Write-Host ""
            Write-Host "NOTE: PATH has been updated. You must open a new terminal for the changes to take effect." -ForegroundColor Yellow
        }
        
        # Check for Npcap
        if (-not (Test-NpcapInstalled)) {
            Write-Host ""
            Write-Host "Windows Runtime Requirement - Npcap:" -ForegroundColor Yellow
            Write-Host "  OpenEC requires Npcap for packet capture, even for offline pcap file analysis." -ForegroundColor Yellow
            Write-Host "  Npcap's free license prohibits bundling and silent installation." -ForegroundColor Yellow
            Write-Host ""
            Write-Host "  Please install Npcap manually from: https://npcap.com/" -ForegroundColor Cyan
            Write-Host "  During installation, enable 'WinPcap API-compatible mode'." -ForegroundColor Cyan
        }
        
        Write-Host ""
        Write-Info "Run '$BinaryName --help' to get started."
    }
    finally {
        # Cleanup
        if (Test-Path $tempDir) {
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Run installer
Install-OpenEC
