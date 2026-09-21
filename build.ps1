# Builds dist\burnit.exe.
#
# Targets .NET Framework 4.x, which every Windows 10/11 box already has. That is a
# deliberate choice rather than laziness: IMAPI2 delivers write progress through a
# COM connection point, and only the .NET Framework CLR gives a managed object an
# automatic IDispatch implementation, which is what the burn engine calls back into.
#
# Prefers the Roslyn compiler from a dotnet SDK when one is installed (nicer
# diagnostics); falls back to the csc.exe that ships inside Windows, so this builds
# on a machine with no developer tools at all.

[CmdletBinding()]
param(
    [switch]$Run,
    [string[]]$Args = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$exe  = Join-Path $dist 'burnit.exe'

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'mscorlib.dll'))) {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
if (-not (Test-Path (Join-Path $fw 'mscorlib.dll'))) {
    throw ".NET Framework 4 runtime assemblies not found under $env:WINDIR\Microsoft.NET."
}

$refs = @('mscorlib.dll','System.dll','System.Core.dll','Microsoft.CSharp.dll') |
        ForEach-Object { "/r:$(Join-Path $fw $_)" }

$sources = Get-ChildItem -Path $src -Filter *.cs | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No sources in $src" }

$common = @(
    '/nologo','/noconfig','/nostdlib+','/target:exe','/platform:anycpu',
    '/optimize+','/warn:3','/unsafe-',"/out:$exe"
) + $refs + $sources

# Roslyn from a dotnet SDK, if present.
$roslyn = $null
$dotnetRoot = Join-Path $env:ProgramFiles 'dotnet\sdk'
if (Test-Path $dotnetRoot) {
    $roslyn = Get-ChildItem $dotnetRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

Write-Host ''
if ($roslyn) {
    Write-Host '  compiling with Roslyn' -ForegroundColor DarkGray
    & dotnet exec $roslyn @common
} else {
    $csc = Join-Path $fw 'csc.exe'
    if (-not (Test-Path $csc)) { throw "No C# compiler found (looked for $csc)." }
    Write-Host '  compiling with in-box csc' -ForegroundColor DarkGray
    & $csc @common
}

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  build failed' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "  built $exe" -ForegroundColor Green
Write-Host ''

if ($Run) { & $exe @Args }
