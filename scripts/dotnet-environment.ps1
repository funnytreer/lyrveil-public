function Get-LyrveilDotnet {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $localSdk = Join-Path $RepositoryRoot '.tools\dotnet\dotnet.exe'
    $systemDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    $candidates = @($localSdk)
    if ($systemDotnet) { $candidates += $systemDotnet.Source }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = @(& $candidate --list-sdks)
        $supported = @($sdks | Where-Object { $_ -match '^\s*(\d+)\.' -and [int]$Matches[1] -ge 8 })
        if ($LASTEXITCODE -eq 0 -and $supported.Count -gt 0) {
            $env:DOTNET_CLI_HOME = Join-Path $RepositoryRoot '.tools\dotnet-home'
            $env:NUGET_PACKAGES = Join-Path $RepositoryRoot '.tools\nuget-packages'
            $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            return $candidate
        }
    }
    throw '未找到 .NET SDK。请安装 .NET 8 或更新的 SDK（仅安装运行时不够），或把便携式 SDK 放入 .tools\dotnet 后重试。'
}
