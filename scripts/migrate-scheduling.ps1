# 从私有环境文件加载 Scheduling 连接，只运行迁移，不启动 HTTP 或配置中心。
param([string] $EnvFile = (Join-Path $PSScriptRoot '../env/platform.dev'))

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EnvFile) {
    foreach ($line in Get-Content -LiteralPath $EnvFile -Encoding UTF8) {
        if ($line -match '^\s*ConnectionStrings__Scheduling=(.*)$') {
            [Environment]::SetEnvironmentVariable('ConnectionStrings__Scheduling', $Matches[1].Trim(), 'Process')
        }
    }
}
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__Scheduling)) {
    Write-Error '请在私有环境文件或部署环境中配置 ConnectionStrings__Scheduling。'
    exit 1
}
$project = Join-Path $PSScriptRoot '../src/Hosts/NexusStackNext.PlatformHost'
& dotnet run --project $project --no-launch-profile -- migrate-scheduling
exit $LASTEXITCODE
