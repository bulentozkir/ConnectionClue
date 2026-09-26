<#
.SYNOPSIS
  Builds release packages: one .msixbundle (x64 + arm64) and one .msi per architecture.

.DESCRIPTION
  1. Publishes the app self-contained for each architecture.
  2. Packs an .msix per architecture with makeappx (Microsoft.Windows.SDK.BuildTools from NuGet, no Windows SDK install)
     and bundles them.
  3. Builds an .msi per architecture with WiX 5 (pinned in dotnet-tools.json).
  4. Signs everything with -CertificateThumbprint, or with a self-signed test certificate (CurrentUser\My) whose
     subject equals -Publisher. Test-signed packages install only where that certificate is trusted.
  5. Writes winget manifests (winget\manifests\...) for the MSIs: after the GitHub release v<Version> is published with
     these files, submit them to microsoft/winget-pkgs so "winget upgrade --all" delivers new versions. Store installs
     update through the Store.
  6. Archives symbols and writes SHA256SUMS.txt.

  For the Store, pass the Partner Center identity (-IdentityName, -Publisher, -PublisherDisplayName) and upload the
  bundle; the Store re-signs it.

.EXAMPLE
  pwsh tools/build-release.ps1 -Version 1.0.0
#>
param(
    [string]$Version = '1.0.2',
    [string]$IdentityName = 'ConnectionClue',
    [string]$Publisher = 'CN=ConnectionClue Test',
    [string]$PublisherDisplayName = 'ConnectionClue',
    [string]$CertificateThumbprint,
    [switch]$NoSign,
    [string[]]$Architectures = @('x64', 'arm64'),
    # winget requires a license name; replace it with the project's actual license before submitting.
    [string]$WingetLicense = 'Proprietary'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'generate-icons.ps1')
$out = Join-Path $root "releases\$Version"
$work = Join-Path $root "artifacts\obj\$Version"
$buildToolsVersion = '10.0.28000.2705'
$packageVersion = "$Version.0"

function Invoke-Tool([string]$exe, [string[]]$arguments, [switch]$Quiet) {
    $output = & $exe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 30 | Write-Host
        throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE"
    }
    if (-not $Quiet) { $output | Where-Object { "$_" -match 'warning|error' } | Write-Host }
}

function Get-SdkBuildTools {
    $cache = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools\$buildToolsVersion"
    if (-not (Test-Path $cache)) {
        # Restore through NuGet (same feed and proxy settings as the build). The project lives outside the repo
        # so central package management does not apply.
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "cc-buildtools-$buildToolsVersion"
        New-Item -ItemType Directory -Force $tmp | Out-Null
        Set-Content (Join-Path $tmp 'tools.csproj') @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="$buildToolsVersion" /></ItemGroup>
</Project>
"@
        Invoke-Tool 'dotnet' @('restore', (Join-Path $tmp 'tools.csproj'), '--nologo', '-v', 'q')
        Remove-Item $tmp -Recurse -Force
    }
    $makeAppx = Get-ChildItem $cache -Recurse -Filter makeappx.exe | Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $makeAppx) { throw "makeappx.exe not found in $cache" }
    @{ MakeAppx = $makeAppx.FullName; SignTool = Join-Path $makeAppx.DirectoryName 'signtool.exe' }
}

function Get-MsiProductCode([string]$path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $view = $record = $null
    try {
        $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($path, 0))
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property = 'ProductCode'"))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    }
    finally {
        foreach ($o in @($record, $view, $db, $installer)) { if ($o) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) | Out-Null } }
    }
}

function Write-Utf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false)) }

function Write-WingetManifests {
    $id = 'ConnectionClue.ConnectionClue'
    $schema = '1.6.0'
    $dir = Join-Path $out "winget\manifests\c\ConnectionClue\ConnectionClue\$Version"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $installers = foreach ($msi in $msis) {
        $name = [IO.Path]::GetFileName($msi)
        $arch = [regex]::Match($name, '-(x64|arm64)\.msi$').Groups[1].Value
        @"
  - Architecture: $arch
    InstallerUrl: https://github.com/bulentozkir/ConnectionClue/releases/download/v$Version/$name
    InstallerSha256: $((Get-FileHash $msi -Algorithm SHA256).Hash)
    ProductCode: '$(Get-MsiProductCode $msi)'
"@
    }
    Write-Utf8 (Join-Path $dir "$id.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@
    Write-Utf8 (Join-Path $dir "$id.installer.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: wix
Scope: machine
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
AppsAndFeaturesEntries:
  - UpgradeCode: '{7C6F1D5E-3B8A-4F2C-9D41-5A0E8B2C6F13}'
Installers:
$($installers -join "`n")
ManifestType: installer
ManifestVersion: $schema
"@
    Write-Utf8 (Join-Path $dir "$id.locale.en-US.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: $PublisherDisplayName
PublisherUrl: https://github.com/bulentozkir/ConnectionClue
PackageName: ConnectionClue
PackageUrl: https://github.com/bulentozkir/ConnectionClue
License: $WingetLicense
ShortDescription: Finds out why your connection lags, buffers or drops, and tells you what to do about it.
Moniker: connectionclue
Tags:
  - network
  - wi-fi
  - diagnostics
  - latency
  - dns
  - accessibility
ReleaseNotesUrl: https://github.com/bulentozkir/ConnectionClue/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@
}

function Get-SigningCertificate {
    if ($CertificateThumbprint) { return Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" }
    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $Publisher -and $_.FriendlyName -eq 'ConnectionClue test signing' -and $_.NotAfter -gt (Get-Date).AddDays(7) -and $_.HasPrivateKey
    } | Select-Object -First 1
    if ($existing) { return $existing }
    Write-Host "Creating self-signed test certificate '$Publisher' in Cert:\CurrentUser\My"
    New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 `
        -FriendlyName 'ConnectionClue test signing' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

Remove-Item $out, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, $work | Out-Null
$sdk = Get-SdkBuildTools
Invoke-Tool 'dotnet' @('tool', 'restore', '--tool-manifest', (Join-Path $root 'dotnet-tools.json'))

$bundleDir = Join-Path $work 'bundle'
New-Item -ItemType Directory -Force $bundleDir | Out-Null
$template = Get-Content (Join-Path $root 'packaging\msix\AppxManifest.xml') -Raw
$msis = @()

foreach ($arch in $Architectures) {
    Write-Host "== $arch"
    $publish = Join-Path $work "publish\$arch"
    Invoke-Tool 'dotnet' @('publish', (Join-Path $root 'src\ConnectionClue.App\ConnectionClue.App.csproj'), '-c', 'Release',
        '-r', "win-$arch", '--self-contained', 'true', "-p:Version=$Version", '-o', $publish, '--nologo', '-v', 'q')

    # MSIX layout: app files (no symbols) + manifest + visual assets.
    $layout = Join-Path $work "msix\$arch"
    New-Item -ItemType Directory -Force $layout | Out-Null
    Copy-Item "$publish\*" $layout -Recurse -Exclude '*.pdb'
    Copy-Item (Join-Path $root 'packaging\msix\Assets') $layout -Recurse
    $manifest = $template.Replace('{Name}', $IdentityName).Replace('{Publisher}', [Security.SecurityElement]::Escape($Publisher)).
        Replace('{PublisherDisplayName}', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('{Version}', $packageVersion).Replace('{Arch}', $arch)
    Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding utf8
    Invoke-Tool $sdk.MakeAppx @('pack', '/d', $layout, '/p', (Join-Path $bundleDir "ConnectionClue_${packageVersion}_$arch.msix"), '/o') -Quiet

    # MSI.
    $msi = Join-Path $out "ConnectionClue-$Version-$arch.msi"
    Invoke-Tool 'dotnet' @('tool', 'run', 'wix', '--', 'build', (Join-Path $root 'packaging\msi\ConnectionClue.wxs'), '-arch', $arch,
        '-d', "Version=$Version", '-d', "PublishDir=$publish", '-d', "AppIcon=$(Join-Path $root 'src\ConnectionClue.App\Assets\ConnectionClue.ico')",
        '-o', $msi, '-nologo')
    Remove-Item ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
    $msis += $msi

    Compress-Archive -Path (Get-ChildItem $publish -Recurse -Filter '*.pdb').FullName -DestinationPath (Join-Path $out "ConnectionClue-$Version-$arch-symbols.zip") -Force
}

$bundle = Join-Path $out "ConnectionClue_$packageVersion.msixbundle"
Invoke-Tool $sdk.MakeAppx @('bundle', '/d', $bundleDir, '/p', $bundle, '/bv', $packageVersion, '/o') -Quiet

if (-not $NoSign) {
    $cert = Get-SigningCertificate
    Invoke-Tool $sdk.SignTool (@('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, '/d', 'ConnectionClue', $bundle) + $msis)
    if (-not $CertificateThumbprint) {
        Export-Certificate -Cert $cert -FilePath (Join-Path $out 'ConnectionClue-test-signing.cer') | Out-Null
    }
}

# After signing: winget verifies the hash of the file users download.
Write-WingetManifests

Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem $out -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
