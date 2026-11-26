param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoRestore,
    [switch]$SkipUi
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet-environment.ps1')
$dotnet = Get-LyrveilDotnet -RepositoryRoot $repositoryRoot
$buildArguments = @('build', (Join-Path $repositoryRoot 'LyricsXQuiet.sln'), '-c', $Configuration, '-v', 'minimal')
if ($NoRestore) { $buildArguments += '--no-restore' }
& $dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$suites = @('CoreSmokeTests', 'PlatformSmokeTests', 'LyricsUpgradeSmokeTests')
if (-not $SkipUi) { $suites += 'OverlayPositionSmokeTests' }
foreach ($suite in $suites) {
    $project = Join-Path $repositoryRoot "tools\$suite\$suite.csproj"
    & $dotnet run --project $project -c $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host '验证完成：构建及所有选定测试通过。'
