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

.PARAMETER Culture
    Which installer UI language to build. A bundle's strings are baked in at build time, so this
    selects one; both cultures are authored and either can be produced from the same tree.

.EXAMPLE
    pwsh ./scripts/build-release.ps1
    pwsh ./scripts/build-release.ps1 -Culture en-US
#>
[CmdletBinding()]
param(
    [string] $Version,
    [ValidateSet('pt-BR', 'en-US')]
    [string] $Culture = 'pt-BR',
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'NutManager.sln'
$artifacts = Join-Path $repositoryRoot 'artifacts'
$staging = Join-Path $artifacts 'staging'
$branding = Join-Path $repositoryRoot 'src\NutManager.App\Assets\Branding\NutManager.ico'

# The installer's artwork is the high-resolution PNG, not the icon. Scaling a 256px .ico frame up to
# fill the header is what made the previous bundles look pixellated; the .ico keeps the jobs an icon
# is actually for, which is the bundle executable and the Add/Remove Programs entry.
$brandingLogo = Join-Path $repositoryRoot 'src\NutManager.App\Assets\Branding\NutManager.png'

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

Write-Host "Building NutManager $Version ($Culture)" -ForegroundColor Cyan

if (-not (Get-Command wix -ErrorAction SilentlyContinue))
{
    throw 'WiX was not found. Install it with: dotnet tool install --global wix --version 5.0.2'
}

# Extensions are separate installs from the toolset and their absence surfaces late - the packages
# build, then the bundle fails. Provisioning them here keeps a build agent and a workstation on the
# same footing, and each version is pinned to the toolset's for the same reason the toolset is.
foreach ($extension in @('WixToolset.BootstrapperApplications.wixext', 'WixToolset.Netfx.wixext'))
{
    if (-not (wix extension list --global 2>$null | Select-String -SimpleMatch $extension -Quiet))
    {
        Write-Host "Adding the $extension extension" -ForegroundColor Cyan
        wix extension add --global "$extension/5.0.2"
        if ($LASTEXITCODE -ne 0) { throw "Failed to add the $extension extension." }
    }
}

# The Terms shown by the installer are generated from the canonical Markdown under docs/. Checking
# rather than regenerating is deliberate: a release must ship the reviewed text that was committed,
# not whatever a build machine happens to produce from an edit nobody looked at.
& (Join-Path $PSScriptRoot 'build-terms-rtf.ps1') -Check

$themeLocalizationDesktop = Join-Path $repositoryRoot "installer\Common\Theme\Desktop.$Culture.wxl"
$themeLocalizationAgent = Join-Path $repositoryRoot "installer\Common\Theme\Agent.$Culture.wxl"
$termsFile = Join-Path $repositoryRoot "installer\Common\Terms\Terms.$Culture.rtf"

foreach ($required in @($brandingLogo, $themeLocalizationDesktop, $themeLocalizationAgent, $termsFile))
{
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing installer input: $required" }
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
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Output,
        # No default. The two products deploy differently on purpose, and a default here is exactly
        # how they would drift back into being the same - which is the failure this parameter exists
        # to make impossible to reach by accident.
        [Parameter(Mandatory)] [bool] $SelfContained
    )

    $selfContainedArgument = if ($SelfContained) { 'true' } else { 'false' }

    dotnet publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained $selfContainedArgument `
        --output $Output `
        -p:NutManagerVersion=$Version `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Project." }
}

$desktopPublish = Join-Path $staging 'desktop'
$agentPublish = Join-Path $staging 'agent'

# Self-contained: an operator installs one thing and no runtime. The cost is real and belongs in the
# record rather than a footnote - a runtime security fix now requires a NutManager release, because
# nothing else on the machine can patch a runtime the product carries privately. For a desktop
# application that trade is worth it; for the Agent, below, it is not.
Publish-Product -Project (Join-Path $repositoryRoot 'src\NutManager.App\NutManager.App.csproj') `
                -Output $desktopPublish -SelfContained $true

# Framework-dependent: the Agent is a long-lived service on a server, so it uses the machine's shared
# ASP.NET Core runtime and inherits its servicing. The Agent bundle detects that runtime and offers
# to install the official Microsoft package when it is missing.
Publish-Product -Project (Join-Path $repositoryRoot 'src\NutManager.Agent\NutManager.Agent.csproj') `
                -Output $agentPublish -SelfContained $false

foreach ($expected in @(
    @{ Path = (Join-Path $desktopPublish 'NutManager.App.exe'); Name = 'desktop application' },
    @{ Path = (Join-Path $agentPublish 'NutManager.Agent.exe'); Name = 'agent service' }))
{
    if (-not (Test-Path -LiteralPath $expected.Path))
    {
        throw "The published $($expected.Name) executable is missing."
    }
}

# Proof rather than inference. A framework-dependent publish must not carry the private runtime that
# the self-contained one did, and file count or directory size would not tell you that reliably - the
# named runtime files either are there or they are not.
$privateRuntimeMarkers = @('hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'Microsoft.AspNetCore.dll')
$leaked = $privateRuntimeMarkers | Where-Object { Test-Path -LiteralPath (Join-Path $agentPublish $_) }
if ($leaked)
{
    throw "The agent publish is carrying a private runtime payload: $($leaked -join ', ')"
}
Write-Host '  agent publish carries no private .NET shared framework' -ForegroundColor Green

# The framework-dependent payload's generated runtimeconfig is the executable contract. Keep the
# installer prerequisite chain aligned with it rather than inferring requirements from project
# references, which can change how the SDK writes the final framework list.
$agentRuntimeConfigPath = Join-Path $agentPublish 'NutManager.Agent.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $agentRuntimeConfigPath))
{
    throw 'The agent runtimeconfig is missing from the framework-dependent publish.'
}

$agentRuntimeConfig = Get-Content -LiteralPath $agentRuntimeConfigPath -Raw | ConvertFrom-Json
$agentFrameworks = @($agentRuntimeConfig.runtimeOptions.frameworks)
foreach ($requiredFramework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'))
{
    $match = @($agentFrameworks | Where-Object { $_.name -eq $requiredFramework })
    if ($match.Count -ne 1 -or -not ([string] $match[0].version).StartsWith('10.', [StringComparison]::Ordinal))
    {
        throw "The agent runtimeconfig does not require exactly one compatible $requiredFramework 10.x framework."
    }
}
Write-Host '  agent runtimeconfig requires Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.x' -ForegroundColor Green

# ---------------------------------------------------------------- installers

function Build-Installer
{
    param(
        [string] $Name,
        [string] $Directory,
        [string] $PublishDir,
        [string] $SetupName,
        [string] $ThemeFile,
        [string] $ThemeLocalization,
        [string[]] $Extensions
    )

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
    $extensionArguments = @()
    foreach ($extension in $Extensions) { $extensionArguments += @('-ext', $extension) }

    wix build (Join-Path $source 'Bundle.wxs') `
        -arch x64 `
        @extensionArguments `
        -d "Version=$Version" `
        -d "PackagePath=$msi" `
        -d "BrandingIcon=$branding" `
        -d "BrandingLogo=$brandingLogo" `
        -d "ThemeFile=$ThemeFile" `
        -d "ThemeLocalization=$ThemeLocalization" `
        -d "TermsFile=$termsFile" `
        -o $setup
    if ($LASTEXITCODE -ne 0) { throw "The $Name bundle failed to build." }

    Write-Host "  $SetupName" -ForegroundColor Green
}

Build-Installer -Name 'NutManager' -Directory 'Desktop' -PublishDir $desktopPublish `
                -SetupName "NutManager-Setup-$Version.exe" `
                -ThemeFile (Join-Path $repositoryRoot 'installer\Common\Theme\DesktopTheme.xml') `
                -ThemeLocalization $themeLocalizationDesktop `
                -Extensions @('WixToolset.BootstrapperApplications.wixext')

# The Agent bundle additionally needs the Netfx extension, which is where DotNetCoreSearch lives.
Build-Installer -Name 'NutManager.Agent' -Directory 'Agent' -PublishDir $agentPublish `
                -SetupName "NutManager-Agent-Setup-$Version.exe" `
                -ThemeFile (Join-Path $repositoryRoot 'installer\Common\Theme\AgentTheme.xml') `
                -ThemeLocalization $themeLocalizationAgent `
                -Extensions @('WixToolset.BootstrapperApplications.wixext', 'WixToolset.Netfx.wixext')

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
