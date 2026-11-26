param(
    [switch]$ShowPanel,
    [switch]$ShowSettings
)

& (Join-Path $PSScriptRoot 'run-app.ps1') -Demo -ShowPanel:$ShowPanel -ShowSettings:$ShowSettings
