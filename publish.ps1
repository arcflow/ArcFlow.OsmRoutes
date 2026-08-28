<#
  publish.ps1 — build ArcFlow.OsmRoutes with `dotnet publish -c Release`.
  Mirrors the repo's translate.ps1 convention.

  Defaults: framework-dependent win-x64 (needs the .NET 8 runtime on the box).
  Params:
    -Runtime         RID, e.g. win-x64 (default), linux-x64. Pass '' to skip -r.
    -SelfContained   include the runtime so no shared .NET is required.
    -Output          output dir (default: bin\publish).
#>
[CmdletBinding()]
param(
  [string]$Runtime = 'win-x64',
  [switch]$SelfContained,
  [string]$Output = "$PSScriptRoot\bin\publish"
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'ArcFlow.OsmRoutes.csproj'

$args = @('publish', $proj, '-c', 'Release', '-o', $Output)
if ($Runtime) { $args += @('-r', $Runtime) }
$args += "--self-contained:$([bool]$SelfContained)".ToLower()

Write-Host "dotnet $($args -join ' ')"
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Host "Published -> $Output"
