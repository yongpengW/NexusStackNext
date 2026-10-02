# 从私有环境文件加载 Platform 连接，只运行迁移，不启动 HTTP 或配置中心。
param([string] $EnvFile = (Join-Path $PSScriptRoot '../env/platform.dev'))

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EnvFile) {
    foreach ($line in Get-Content -LiteralPath $EnvFile -Encoding UTF8) {
        if ($line -match '^\s*ConnectionStrings__Platform=(.*)$') {
            [Environment]::SetEnvironmentVariable('ConnectionStrings__Platform', $Matches[1].Trim(), 'Process')
        }
    }
}
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__Platform)) {
    Write-Error '请在私有环境文件或部署环境中配置 ConnectionStrings__Platform。'
    exit 1
}
$project = Join-Path $PSScriptRoot '../src/Hosts/NexusStackNext.PlatformHost'
& dotnet run --project $project --no-launch-profile -- migrate-platform
exit $LASTEXITCODE
