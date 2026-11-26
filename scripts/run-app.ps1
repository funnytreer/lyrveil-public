param(
    [switch]$Demo,
    [switch]$ShowPanel,
    [switch]$ShowSettings,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet-environment.ps1')
$dotnet = Get-LyrveilDotnet -RepositoryRoot $repoRoot
$project = Join-Path $repoRoot 'src\LyricsXQuiet.App\LyricsXQuiet.App.csproj'

& $dotnet build $project --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$framework = 'net8.0-windows10.0.19041.0'
$buildDirectory = Join-Path $repoRoot "src\LyricsXQuiet.App\bin\$Configuration\$framework"
$stableDirectory = Join-Path $repoRoot 'artifacts\current'
$executable = Join-Path $stableDirectory 'LyricsXQuiet.App.exe'
$appArguments = [System.Collections.Generic.List[string]]::new()
if ($Demo) { $appArguments.Add('--demo') }
if ($ShowPanel) { $appArguments.Add('--show-panel') }
if ($ShowSettings) { $appArguments.Add('--show-settings') }

# Windows remembers a notification icon by its executable identity. Keep every
# development deployment at one stable path so rebuilding does not create a new
# overflow entry in the system tray.
$repoPrefix = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
# Stop the monitor before replacing files; otherwise it can recreate the main
# process while the old instances are being stopped and lock the deployment.
$deploymentStopSignal = [System.Threading.EventWaitHandle]::new(
    $false, [System.Threading.EventResetMode]::ManualReset, 'Local\Lyrveil.PlayerMonitor.Stop')
try {
    $deploymentDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $deploymentStopSignal.Set() | Out-Null
        $deploymentProcesses = @(Get-Process -Name 'LyricsXQuiet.App' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith(
                        $repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)
                }
                catch { $false }
            })
        if ($deploymentProcesses.Count -eq 0) { break }
        $deploymentProcesses | Stop-Process -ErrorAction SilentlyContinue
        $deploymentProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
        if ([DateTime]::UtcNow -gt $deploymentDeadline) { throw '词间仍在运行，无法替换程序文件。' }
    } while ($true)

    New-Item -ItemType Directory -Path $stableDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $buildDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stableDirectory -Recurse -Force
    }

    if ($appArguments.Count -gt 0) {
        Start-Process -FilePath $executable -ArgumentList $appArguments -WindowStyle Hidden
    }
    else {
        Start-Process -FilePath $executable -WindowStyle Hidden
    }
}
finally { $deploymentStopSignal.Dispose() }
