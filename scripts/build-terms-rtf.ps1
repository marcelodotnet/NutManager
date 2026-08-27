<#
.SYNOPSIS
    Regenerates the installer's Terms of Use from the canonical Markdown.

.DESCRIPTION
    The installers display the Terms as RTF because that is what Burn's rich-edit control reads, and
    the Terms must be readable with no network. The canonical text, however, lives in Markdown under
    docs/ so it can be reviewed, diffed and published like any other document.

    That leaves two representations of one legal text, which is a drift risk worth taking seriously:
    the copy the operator actually accepts is the one in the installer, so a stale RTF means people
    agreed to something other than what was published. This script is the single direction of that
    relationship - docs/ is the source, installer/Common/Terms/ is generated - and the output is
    committed so an offline build never has to regenerate it.

    Deterministic by construction: no timestamps, no culture-dependent formatting, no randomness, no
    external converter. The same Markdown produces the same bytes on any machine, which is what lets
    -Check compare the committed RTF against a fresh conversion and fail the build on drift.

    Deliberately a small subset of Markdown - headings, bold, bullets, numbered items, inline code -
    because that is all the Terms use. Anything richer belongs in the published document, not in a
    licence pane.

.EXAMPLE
    pwsh ./scripts/build-terms-rtf.ps1
    pwsh ./scripts/build-terms-rtf.ps1 -Check
#>
[CmdletBinding()]
param(
    # Verify the committed RTF matches the Markdown instead of rewriting it. Used by the build.
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$termsDirectory = Join-Path $repositoryRoot 'installer\Common\Terms'

$documents = @(
    @{ Culture = 'pt-BR'; Source = 'docs\TERMS-OF-USE.md' },
    @{ Culture = 'en-US'; Source = 'docs\TERMS-OF-USE.en-US.md' }
)

# RTF's escape set. Backslash and braces are structural, so a literal one in the text has to be
# escaped or the document stops parsing at that character.
function ConvertTo-RtfText
{
    param([string] $Value)

    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $Value.ToCharArray())
    {
        if ($character -eq '\')     { [void]$builder.Append('\\'); continue }
        if ($character -eq '{')     { [void]$builder.Append('\{'); continue }
        if ($character -eq '}')     { [void]$builder.Append('\}'); continue }

        $code = [int]$character
        if ($code -lt 128)
        {
            [void]$builder.Append($character)
        }
        else
        {
            # \uN? is the portable form: the numeric code point followed by a replacement character
            # for readers that predate Unicode RTF. Portuguese needs this on almost every paragraph,
            # so getting it wrong is not a corner case.
            [void]$builder.Append('\u').Append($code).Append('?')
        }
    }

    return $builder.ToString()
}

# Inline Markdown, applied after escaping so the markers themselves cannot be mistaken for content.
function ConvertTo-RtfInline
{
    param([string] $Value)

    $text = ConvertTo-RtfText -Value $Value

    # **bold** -> \b ... \b0 . The trailing space after \b0 terminates the control word.
    $text = [regex]::Replace($text, '\*\*(.+?)\*\*', '\b $1\b0 ')

    # `code` keeps its characters and loses its backticks; the licence pane has one font.
    $text = [regex]::Replace($text, '`(.+?)`', '$1')

    return $text
}

function ConvertTo-Rtf
{
    param([string[]] $Lines)

    $body = [System.Text.StringBuilder]::new()
    $inComment = $false

    foreach ($line in $Lines)
    {
        $trimmed = $line.Trim()

        # The Markdown carries maintenance notes for the repository. They are not part of the legal
        # text and must not reach the operator.
        if ($inComment)
        {
            if ($trimmed -match '-->') { $inComment = $false }
            continue
        }
        if ($trimmed -match '^<!--')
        {
            if ($trimmed -notmatch '-->') { $inComment = $true }
            continue
        }

        # Markdown separates paragraphs with a blank line; RTF separates them with \sa on the
        # paragraph itself. Emitting a \par for the blank line as well produces an empty paragraph on
        # top of that spacing, which doubles every gap and makes the document about half again as
        # long - so the reader has to scroll further to reach the same text.
        if ($trimmed.Length -eq 0)
        {
            continue
        }

        if ($trimmed -match '^##\s+(.*)$')
        {
            [void]$body.AppendLine(('\pard\sb180\sa90\b\fs24 {0}\b0\fs20\par' -f (ConvertTo-RtfInline $Matches[1])))
            continue
        }

        if ($trimmed -match '^#\s+(.*)$')
        {
            [void]$body.AppendLine(('\pard\sa180\b\fs32 {0}\b0\fs20\par' -f (ConvertTo-RtfInline $Matches[1])))
            continue
        }

        if ($trimmed -match '^-\s+(.*)$')
        {
            # An indented bullet rather than a real RTF list: \pntext lists render inconsistently
            # across rich-edit versions, and a wrong-looking bullet in a legal pane is a support call.
            [void]$body.AppendLine(('\pard\fi-200\li480\sa40\bullet\tab {0}\par' -f (ConvertTo-RtfInline $Matches[1])))
            continue
        }

        if ($trimmed -match '^([0-9]+)\.\s+(.*)$')
        {
            [void]$body.AppendLine(('\pard\fi-200\li480\sa40 {0}.\tab {1}\par' -f $Matches[1], (ConvertTo-RtfInline $Matches[2])))
            continue
        }

        [void]$body.AppendLine(('\pard\sa120 {0}\par' -f (ConvertTo-RtfInline $trimmed)))
    }

    $header = '{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}' + "`r`n" +
              '\viewkind4\uc1\pard\f0\fs20' + "`r`n"

    return $header + $body.ToString() + '}' + "`r`n"
}

if (-not (Test-Path -LiteralPath $termsDirectory))
{
    New-Item -ItemType Directory -Force -Path $termsDirectory | Out-Null
}

$drift = @()

foreach ($document in $documents)
{
    $source = Join-Path $repositoryRoot $document.Source
    if (-not (Test-Path -LiteralPath $source))
    {
        throw "The canonical Terms document is missing: $($document.Source)"
    }

    $lines = Get-Content -LiteralPath $source -Encoding UTF8
    $rtf = ConvertTo-Rtf -Lines $lines
    $target = Join-Path $termsDirectory ("Terms.{0}.rtf" -f $document.Culture)

    # ASCII on purpose. Every non-ASCII character has already become a \uN? escape, so the file has
    # no encoding to get wrong and no byte-order mark to confuse the rich-edit control.
    if ($Check)
    {
        if (-not (Test-Path -LiteralPath $target))
        {
            $drift += "Missing: $target"
            continue
        }

        $existing = [System.IO.File]::ReadAllText($target, [System.Text.Encoding]::ASCII)
        if ($existing -ne $rtf)
        {
            $drift += "Out of date with $($document.Source): $target"
        }
        continue
    }

    [System.IO.File]::WriteAllText($target, $rtf, [System.Text.Encoding]::ASCII)
    Write-Host ("  {0}  <-  {1}" -f (Split-Path -Leaf $target), $document.Source) -ForegroundColor Green
}

if ($drift)
{
    $drift | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw 'The installer Terms are out of date. Run scripts/build-terms-rtf.ps1 and commit the result.'
}

if ($Check)
{
    Write-Host 'Installer Terms match the canonical documents.' -ForegroundColor Green
}
