# 只读取来源 journal 连接并迁移；不启动业务、HTTP、配置中心或消息消费。
param([string] $EnvFile = (Join-Path $PSScriptRoot '../env/platform.dev'))

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EnvFile) {
    foreach ($line in Get-Content -LiteralPath $EnvFile -Encoding UTF8) {
        if ($line -match '^\s*ConnectionStrings__OperationJournal=(.*)$') {
            [Environment]::SetEnvironmentVariable('ConnectionStrings__OperationJournal', $Matches[1].Trim(), 'Process')
        }
    }
}
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__OperationJournal)) {
    Write-Error '请在私有环境文件或部署环境中配置 ConnectionStrings__OperationJournal。'
    exit 1
}
$project = Join-Path $PSScriptRoot '../src/Hosts/NexusStackNext.PlatformHost'
& dotnet run --project $project --no-launch-profile -- migrate-operation-journal
exit $LASTEXITCODE
