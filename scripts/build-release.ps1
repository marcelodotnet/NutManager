<#
.SYNOPSIS
    Builds every distributable NutManager artifact from a clean tree.

.DESCRIPTION
    Orchestration only. This script publishes, invokes the WiX toolchain and hashes the result; it is
    not the installer and must never become one. Anything that decides what gets installed belongs in
    the .wxs authoring under installer/, where the Windows Installer engine owns it.

    Produces, in artifacts/:

        NutManager-Setup-x.y.z.exe
        NutManager-Agent-Setup-x.y.z.exe
        NutManager-win-x64.zip
        SHA256SUMS.txt

    The zip is the portable copy: it needs no administrator and installs nothing, which is why it
    survives alongside the installers rather than being replaced by them.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'NutManager.sln'
$artifacts = Join-Path $repositoryRoot 'artifacts'
$staging = Join-Path $artifacts 'staging'
$branding = Join-Path $repositoryRoot 'src\NutManager.App\Assets\Branding\NutManager.ico'

# The single version source. Reading it from Directory.Build.props rather than defaulting here is what
# stops the installers and the assemblies from disagreeing about what they are.
if (-not $Version)
{
    $props = Get-Content (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    if ($props -match '<NutManagerVersion[^>]*>([0-9]+\.[0-9]+\.[0-9]+)</NutManagerVersion>')
    {
        $Version = $Matches[1]
    }
    else
    {
        throw 'NutManagerVersion was not found in Directory.Build.props.'
    }
}

Write-Host "Building NutManager $Version" -ForegroundColor Cyan

if (-not (Get-Command wix -ErrorAction SilentlyContinue))
{
    throw 'WiX was not found. Install it with: dotnet tool install --global wix --version 5.0.2'
}

# The bundle extension is a separate install from the toolset, and its absence surfaces late — the
# packages build, then the bundle fails. Provisioning it here keeps a build agent and a workstation on
# the same footing, and the version is pinned to the toolset's for the same reason the toolset is.
$bundleExtension = 'WixToolset.BootstrapperApplications.wixext'
if (-not (wix extension list --global 2>$null | Select-String -SimpleMatch $bundleExtension -Quiet))
{
    Write-Host "Adding the $bundleExtension extension" -ForegroundColor Cyan
    wix extension add --global "$bundleExtension/5.0.2"
    if ($LASTEXITCODE -ne 0) { throw "Failed to add the $bundleExtension extension." }
}

# A stale artifacts directory is how a release ends up shipping a file from the previous version.
if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifacts, $staging | Out-Null

# ---------------------------------------------------------------- build and test

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

dotnet build $solution --configuration Release --no-restore -p:NutManagerVersion=$Version
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not $SkipTests)
{
    dotnet test $solution --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

# ---------------------------------------------------------------- publish

function Publish-Product
{
    param([string] $Project, [string] $Output)

    # Both products are self-contained: the desktop application so an operator installs one thing and
    # no runtime, the agent so a server needs no ASP.NET Core runtime of its own. The cost is real and
    # belongs in the record rather than a footnote — a runtime security fix now requires a NutManager
    # release, because nothing else on the machine can patch a runtime the product carries privately.
    dotnet publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $Output `
        -p:NutManagerVersion=$Version `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Project." }
}

$desktopPublish = Join-Path $staging 'desktop'
$agentPublish = Join-Path $staging 'agent'

Publish-Product -Project (Join-Path $repositoryRoot 'src\NutManager.App\NutManager.App.csproj') -Output $desktopPublish
Publish-Product -Project (Join-Path $repositoryRoot 'src\NutManager.Agent\NutManager.Agent.csproj') -Output $agentPublish

foreach ($expected in @(
    @{ Path = (Join-Path $desktopPublish 'NutManager.App.exe'); Name = 'desktop application' },
    @{ Path = (Join-Path $agentPublish 'NutManager.Agent.exe'); Name = 'agent service' }))
{
    if (-not (Test-Path -LiteralPath $expected.Path))
    {
        throw "The published $($expected.Name) executable is missing."
    }
}

# ---------------------------------------------------------------- installers

function Build-Installer
{
    param([string] $Name, [string] $Directory, [string] $PublishDir, [string] $SetupName)

    $source = Join-Path $repositoryRoot "installer\$Directory"
    $msi = Join-Path $staging "$Name.msi"

    wix build (Join-Path $source 'Package.wxs') `
        -arch x64 `
        -d "Version=$Version" `
        -d "PublishDir=$PublishDir" `
        -d "BrandingIcon=$branding" `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw "The $Name package failed to build." }

    $setup = Join-Path $artifacts $SetupName

    wix build (Join-Path $source 'Bundle.wxs') `
        -arch x64 `
        -ext WixToolset.BootstrapperApplications.wixext `
        -d "Version=$Version" `
        -d "PackagePath=$msi" `
        -d "BrandingIcon=$branding" `
        -o $setup
    if ($LASTEXITCODE -ne 0) { throw "The $Name bundle failed to build." }

    Write-Host "  $SetupName" -ForegroundColor Green
}

Build-Installer -Name 'NutManager' -Directory 'Desktop' -PublishDir $desktopPublish -SetupName "NutManager-Setup-$Version.exe"
Build-Installer -Name 'NutManager.Agent' -Directory 'Agent' -PublishDir $agentPublish -SetupName "NutManager-Agent-Setup-$Version.exe"

# ---------------------------------------------------------------- portable archive

$zip = Join-Path $artifacts 'NutManager-win-x64.zip'
Compress-Archive -Path (Join-Path $desktopPublish '*') -DestinationPath $zip -CompressionLevel Optimal -Force

# ---------------------------------------------------------------- signing

# Deliberately a stated gap rather than a stub that pretends. No code-signing certificate is available
# to this repository, so these artifacts are unsigned and SmartScreen will warn on first run. When a
# certificate exists, sign here: the MSI inside each bundle first, then the bundle, because signing a
# bundle whose payload changes afterwards invalidates the signature.
Write-Host 'Signing: skipped, no certificate configured. Artifacts are unsigned.' -ForegroundColor Yellow

# ---------------------------------------------------------------- checksums

# WiX drops a .wixpdb beside each output. They are build symbols, not something anyone installs, and
# leaving them here would put them in the checksum file as though they were part of the release.
Get-ChildItem -LiteralPath $artifacts -File -Filter '*.wixpdb' | Remove-Item -Force

# Hashed from the files actually being shipped, after every step that could still rewrite them.
$hashes = Get-ChildItem -LiteralPath $artifacts -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }

Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Value $hashes -Encoding ascii

Remove-Item -LiteralPath $staging -Recurse -Force

Write-Host ''
Write-Host 'Artifacts:' -ForegroundColor Cyan
Get-ChildItem -LiteralPath $artifacts -File | ForEach-Object {
    Write-Host ("  {0,-44} {1,12:N0} bytes" -f $_.Name, $_.Length)
}
