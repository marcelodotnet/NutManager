[CmdletBinding()]
param(
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'

if ([string]::IsNullOrWhiteSpace($Version))
{
    $props = Get-Content -LiteralPath $propsPath -Raw
    if ($props -notmatch '<NutManagerVersion[^>]*>([0-9]+\.[0-9]+\.[0-9]+)</NutManagerVersion>')
    {
        throw 'NutManagerVersion was not found in Directory.Build.props.'
    }

    $Version = $Matches[1]
}

$testRoot = Join-Path $repositoryRoot 'artifacts\test'
$staging = Join-Path $testRoot '.staging'
$agentPublish = Join-Path $staging 'agent'
$packageName = "NutManager-Agent-Test-$Version"
$payload = Join-Path $testRoot $packageName
$zipPath = Join-Path $testRoot "$packageName.zip"
$checksumPath = Join-Path $testRoot 'SHA256SUMS.txt'

# This script owns only artifacts\test. It never examines or removes release artifacts, installed
# files, ProgramData, services or machine configuration.
if (Test-Path -LiteralPath $testRoot)
{
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $agentPublish, $payload | Out-Null

function Publish-Application
{
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Output
    )

    dotnet publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $Output `
        -p:NutManagerVersion=$Version `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0)
    {
        throw "Publish failed for $Project."
    }
}

# One publish, because there is one application.
#
# This used to publish the agent and the configuration utility separately and merge the two trees,
# reconciling every shared assembly by hash along the way. The configuration window is a mode of the
# agent host now, so its assembly arrives as an ordinary dependency of the single publish and there
# is nothing left to merge or to disagree about.
Publish-Application `
    -Project (Join-Path $repositoryRoot 'src\NutManager.Agent\NutManager.Agent.csproj') `
    -Output $agentPublish

foreach ($source in Get-ChildItem -LiteralPath $agentPublish -Recurse -File)
{
    if ($source.Extension -eq '.pdb') { continue }

    $relativePath = [System.IO.Path]::GetRelativePath($agentPublish, $source.FullName)
    $target = Join-Path $payload $relativePath
    $targetDirectory = Split-Path -Parent $target
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    Copy-Item -LiteralPath $source.FullName -Destination $target
}

$requiredFiles = @(
    'NutManager.Agent.exe',
    'NutManager.Agent.dll',
    'NutManager.Agent.deps.json',
    'NutManager.Agent.runtimeconfig.json',
    # The configuration window, as a library. It has no apphost, no deps.json and no runtimeconfig of
    # its own any more: the host it runs inside owns all three.
    'NutManager.Agent.Config.dll'
)
foreach ($requiredFile in $requiredFiles)
{
    if (-not (Test-Path -LiteralPath (Join-Path $payload $requiredFile)))
    {
        throw "Portable Agent payload is missing '$requiredFile'."
    }
}

function Read-Frameworks
{
    param([Parameter(Mandatory)] [string] $RuntimeConfigPath)

    $runtimeConfig = Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
    $frameworks = @()
    $optionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
    if ($optionNames -contains 'framework') { $frameworks += $runtimeConfig.runtimeOptions.framework }
    if ($optionNames -contains 'frameworks') { $frameworks += @($runtimeConfig.runtimeOptions.frameworks) }
    return @($frameworks)
}

$agentFrameworks = @(Read-Frameworks (Join-Path $payload 'NutManager.Agent.runtimeconfig.json'))
foreach ($requiredFramework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'))
{
    $matches = @($agentFrameworks | Where-Object { $_.name -eq $requiredFramework })
    if ($matches.Count -ne 1 -or -not ([string] $matches[0].version).StartsWith('10.', [StringComparison]::Ordinal))
    {
        throw "Agent runtimeconfig must require exactly one compatible $requiredFramework 10.x framework."
    }
}

# One apphost, asserted rather than assumed.
#
# The configuration utility used to ship its own executable beside this one, with its own runtime
# contract. Both are gone, and a payload that still carried either would mean the unified host did
# not actually take over - an operator would find two executables again and have to guess.
$obsoleteApphostFiles = @(
    'NutManager.Agent.Config.exe',
    'NutManager.Agent.Config.deps.json',
    'NutManager.Agent.Config.runtimeconfig.json'
)
foreach ($obsolete in $obsoleteApphostFiles)
{
    if (Test-Path -LiteralPath (Join-Path $payload $obsolete))
    {
        throw "Portable Agent payload still contains the retired Agent Config apphost file '$obsolete'."
    }
}

$apphosts = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Filter '*.exe')
if ($apphosts.Count -ne 1 -or $apphosts[0].Name -ne 'NutManager.Agent.exe')
{
    throw "Portable Agent payload must contain exactly one executable, NutManager.Agent.exe."
}

$privateRuntimeMarkers = @(
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'clrjit.dll',
    'System.Private.CoreLib.dll',
    'Microsoft.AspNetCore.dll'
)
$privateRuntime = Get-ChildItem -LiteralPath $payload -Recurse -File |
    Where-Object { $privateRuntimeMarkers -contains $_.Name }
if ($privateRuntime)
{
    throw "Portable Agent payload contains a private runtime: $($privateRuntime.Name -join ', ')."
}

if (Get-ChildItem -LiteralPath $payload -Recurse -File -Filter '*.pdb')
{
    throw 'Portable Agent payload contains debug symbols.'
}

$forbiddenNames = @('agent.json', 'settings.json')
$forbiddenExtensions = @('.pfx', '.p12', '.pem', '.key', '.cer', '.crt')
$forbiddenPayload = Get-ChildItem -LiteralPath $payload -Recurse -File |
    Where-Object { $forbiddenNames -contains $_.Name -or $forbiddenExtensions -contains $_.Extension }
if ($forbiddenPayload)
{
    throw "Portable Agent payload contains runtime configuration, secrets or certificates: $($forbiddenPayload.Name -join ', ')."
}

Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zipPath -CompressionLevel Optimal

# The archive and the one executable inside it. There used to be two.
$checksumTargets = @(
    Get-Item -LiteralPath $zipPath
    Get-Item -LiteralPath (Join-Path $payload 'NutManager.Agent.exe')
)
$checksumLines = foreach ($target in $checksumTargets)
{
    $hash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = if ($target.FullName -eq $zipPath) { $target.Name } else { "$packageName/$($target.Name)" }
    "$hash  $name"
}
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM

Remove-Item -LiteralPath $staging -Recurse -Force

Write-Host "Portable Agent test payload: $payload" -ForegroundColor Green
Write-Host "Portable Agent test ZIP:     $zipPath" -ForegroundColor Green
Write-Host "Checksums:                    $checksumPath" -ForegroundColor Green
