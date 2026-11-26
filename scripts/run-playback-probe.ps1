[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ProbeArguments
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet-environment.ps1')
$dotnetExecutable = Get-LyrveilDotnet -RepositoryRoot $repositoryRoot
$projectPath = Join-Path $repositoryRoot 'tools\PlaybackProbe\PlaybackProbe.csproj'

& $dotnetExecutable run --project $projectPath -- @ProbeArguments
exit $LASTEXITCODE
