<#
    build.ps1 - zero-install build for EjectScope.

    Uses the C# compiler that ships with the .NET Framework, which is present on
    every Windows 10/11 machine. No SDK, no NuGet restore, no MSBuild.

    This is deliberate. A tool that asks the user to trust it with a list of
    their open files should be rebuildable from an auditable command line on a
    clean box. The same sources also build with the .NET SDK (see
    src/*/*.csproj) for contributors who have one installed; sources are kept
    C# 5 compatible so both compilers accept them.

    Two products come out of this script:

      build\EjectScope.exe      the window (WPF, winexe)
      build\ejectscope-cli.exe  the same engine without a UI (console)

    They are separate executables on purpose: a GUI process has no console, and
    a tool that can silently run from a script is a different security story
    than one a person clicks.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Build only the console version (used by the self test in CI).
    [switch]$CliOnly,

    # Start the GUI (or the CLI with -RunArgs) after a successful build.
    [switch]$Run,
    [string]$RunArgs = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "No in-box C# compiler found. Install the .NET Framework 4.x or the .NET SDK."
}

$frameworkDir = Split-Path -Parent $csc
$wpfDir = Join-Path $frameworkDir 'WPF'

$outDir = Join-Path $root 'build'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$opt = if ($Configuration -eq 'Release') { '/optimize+' } else { '/optimize-' }

function Get-Sources([string]$project) {
    $dir = Join-Path $root (Join-Path 'src' $project)
    $files = @(Get-ChildItem -Path $dir -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName)
    if ($files.Count -eq 0) { throw "No sources found under src/$project." }
    return $files
}

function Invoke-Csc([string]$label, [string]$output, [string[]]$cscArgs) {
    Write-Host "building : $label"
    Write-Host "           -> $output"
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation of $label failed with exit code $LASTEXITCODE."
    }
    $size = (Get-Item $output).Length
    Write-Host ("           OK ({0:N0} bytes)" -f $size)
    Write-Host ''
}

$coreSources = Get-Sources 'EjectScope.Core'
$cliSources = Get-Sources 'EjectScope.Cli'
Write-Host "compiler : $csc"
Write-Host "sources  : core $($coreSources.Count), cli $($cliSources.Count)"
Write-Host ''

# ---------------------------------------------------------------- console exe
$cliOut = Join-Path $outDir 'ejectscope-cli.exe'
$cliArgs = @(
    '/nologo'
    '/codepage:65001'
    '/target:exe'
    '/platform:anycpu'
    $opt
    '/warn:4'
    "/out:$cliOut"
    '/r:System.dll'
    '/r:System.Core.dll'
) + $coreSources + $cliSources
if ($Configuration -ne 'Release') { $cliArgs += '/debug:full' }

Invoke-Csc 'ejectscope-cli.exe' $cliOut $cliArgs

if ($CliOnly) {
    if ($Run) {
        $argv = @()
        if ($RunArgs) { $argv = $RunArgs -split ' ' }
        & $cliOut @argv
        exit $LASTEXITCODE
    }
    exit 0
}

# -------------------------------------------------------------------- wpf exe
$appSources = Get-Sources 'EjectScope.App'
$appOut = Join-Path $outDir 'EjectScope.exe'
$manifest = Join-Path $root 'src\EjectScope.App\app.manifest'

$appArgs = @(
    '/nologo'
    '/codepage:65001'
    '/target:winexe'
    '/platform:anycpu'
    $opt
    '/warn:4'
    "/out:$appOut"
    "/win32manifest:$manifest"
    '/r:System.dll'
    '/r:System.Core.dll'
    "/r:$(Join-Path $wpfDir 'PresentationFramework.dll')"
    "/r:$(Join-Path $wpfDir 'PresentationCore.dll')"
    "/r:$(Join-Path $wpfDir 'WindowsBase.dll')"
    "/r:$(Join-Path $wpfDir 'PresentationUI.dll')"
    "/r:$(Join-Path $frameworkDir 'System.Xaml.dll')"
) + $coreSources + $appSources
if ($Configuration -ne 'Release') { $appArgs += '/debug:full' }

Invoke-Csc 'EjectScope.exe' $appOut $appArgs

if ($Run) {
    Write-Host "starting $appOut"
    Start-Process -FilePath $appOut
}
