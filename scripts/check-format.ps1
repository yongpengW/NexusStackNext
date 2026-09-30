#!/usr/bin/env pwsh
#
# 格式门禁：`dotnet format --verify-no-changes`。
#
# **为什么它值得单独一条。** `.editorconfig` 与 `Directory.Build.props` 声明了格式与风格，
# 而**声明不等于检查**——本仓实测：第一次跑这个命令时全仓有 **448 处违规**
# （363 处行尾标记、69 处空白、10 处 `using` 顺序、6 处缺行尾换行），分布在 **24 个文件**里，
# 而**构建全绿**。其中两个测试文件是 CRLF，而 `.editorconfig` 写的是 `end_of_line = lf`。
#
# 按 `resolving-merge-conflicts` 的三段顺序，它排在最后：
# **typecheck（`dotnet build`）→ tests（`scripts/run-tests.ps1`）→ format（本脚本）**。
param(
    # 就地修，而不是只检查。改完请重新跑一次不带参数的（验证真的干净了）。
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $repoRoot 'NexusStackNext.slnx'

# `dotnet format` 会加载整个解决方案；关掉 MSBuild 节点复用，免得留下常驻进程。
$env:MSBUILDDISABLENODEREUSE = '1'

if ($Fix) {
    Write-Host '就地修格式…' -ForegroundColor Cyan
    dotnet format $solution --no-restore
    exit $LASTEXITCODE
}

Write-Host '检查格式（dotnet format --verify-no-changes）…' -ForegroundColor Cyan
dotnet format $solution --verify-no-changes --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '格式不符合 .editorconfig（上面的 error 就是逐处位置）。' -ForegroundColor Red
    Write-Host '要就地修：pwsh -File scripts/check-format.ps1 -Fix，然后重新跑一次本脚本。' -ForegroundColor Yellow
    exit $LASTEXITCODE
}

Write-Host '格式干净：0 处违规。' -ForegroundColor Green
exit 0
