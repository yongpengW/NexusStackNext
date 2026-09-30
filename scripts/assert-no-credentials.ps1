#!/usr/bin/env pwsh
#
# 断言：模板生成物里不含任何凭据。
#
# 这条检查的存在理由见票据 16：参照仓库的 AgileConfig 客户端会把拉到的配置
# （含数据库口令、Redis 口令、阿里云 AK/SK）落到 agile/config/*.cache，
# 而那些文件会被打进模板包分发给所有使用者。
#
# 它被 CI 调用，所以"模板不含凭据"不是一句承诺，是一条会失败的检查。

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$temp = [System.IO.Path]::GetTempPath()
$hive = Join-Path $temp 'nsn-credential-hive'
$out = Join-Path $temp 'nsn-credential-probe'

Remove-Item $hive, $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '安装模板到隔离 hive…'
dotnet new install $repoRoot --debug:custom-hive $hive | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error '模板安装失败'; exit 1 }

Write-Host '生成探针工程…'
dotnet new nexusstack-next -n CredentialProbe -o $out --debug:custom-hive $hive | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error '模板生成失败'; exit 1 }

# ---------- 先查一件比"含不含凭据"更硬的事 ----------
#
# env/*.env 是**本机**的配置中心密钥。它一旦随模板分发出去，每个用这个模板
# 新建项目的人都会拿到你的密钥——参照仓库正是这么栽的（票据 18）。
#
# 所以这里断言的不是"里面有没有凭据"，而是**这个文件根本不能出现在生成物里**。
# 前两道闸（.gitignore 与模板排除）靠约定，这一道会失败。
# 现在没有"样板文件"了（骨架由 run-host.ps1 -Init 生成），
# 所以生成物里出现**任何** *.dev 都是泄漏。
$leaked = @(Get-ChildItem -Recurse -File $out -Filter '*.dev' -ErrorAction SilentlyContinue)

if ($leaked.Count -gt 0) {
    Write-Host ''
    Write-Host 'env/*.dev 出现在了模板生成物里——它是本机密钥，绝不能分发：' -ForegroundColor Red
    $leaked | ForEach-Object { Write-Host ("  " + $_.FullName.Substring($out.Length + 1)) -ForegroundColor Red }
    Write-Host ''
    Write-Host '检查 .template.config/template.json 的 exclude 是否含 "**/env/*.dev"。' -ForegroundColor Red
    exit 1
}
# 只找"值"，不找"属性名"：AccessKeyId 作为 C# 属性名出现是无害的，
# 而 AccessKeySecret = "<已脱敏>" 这种赋值才是凭据。
$patterns = [ordered]@{
    # **收窄过的。** 原先的 `Password\s*=\s*[^;"\s]{3,}` 把 C# 里的
    # `Password = _options.Password` 也算成了凭据——那是**变量传递**，泄漏不了任何东西。
    # 实测被它绊了五次，每次都是在改代码绕一个正则，而不是在修一个真问题。
    #
    # 现在按兄弟项 `连接串用户名` 的同一判据：要么同行有连接串的关键字，
    # 要么是**连接串写法**（等号两边不留空格）。真被提交的连接串两种都占，
    # 而 C# 赋值两种都不占。
    '连接串口令'     = '(?:(?:Host|Server|Data Source)\s*=[^"\r\n]*?Password\s*=\s*(?!your|<|\$\{)[^;"\s]{3,}|(?<![\w ])Password=(?!your|<|\$\{)[^;"\s]{3,})'
    # 用户名只在**连接串**或**JSON 键**这两种形态下才算凭据。
    # 第一版写成 "Username\s*=\s*..." 会误伤代码里的 `userName = UserName`
    # （Select-String 默认忽略大小写），实测在模板生成物里报了 3 处假阳性。
    '连接串用户名'   = '(?:Host|Server|Data Source)\s*=[^"\r\n]*?(?:Username|User Id|Uid)\s*=\s*(?!your|<|\$\{)[^;"\s]{3,}'
    'JSON 用户名键'  = '"(?:Username|User Id|Uid)"\s*:\s*"(?!your|<|\$\{)[^"]{3,}"'
    'RabbitMQ 凭据'  = 'amqp://[^"\s]*:[^"\s]*@'
    '阿里云 AK'      = '\bLTAI[A-Za-z0-9]{12,}\b'
    '私钥头'         = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    # 原来只管 RFC1918，于是真实导出文件里的公网地址（一个公网 IP 字面量）三次出现却零命中。
    # 改成"任何真实 IP 字面量"，只排除回环与未指定地址——配置里出现主机名是正常的，
    # 出现 IP 字面量则说明环境地址被写死进了仓库。
    '配置里的真实 IP' = '(?<![\d.])(?!127\.0\.0\.1\b)(?!0\.0\.0\.0\b)(?:\d{1,3}\.){3}\d{1,3}(?![\d.])'
    # **拼出来的，不写成明文。** 这条规则要在生成物里找那个样本，所以脚本必须知道它；
    # 但把样本明文写进仓库等于又一次把它发布出去（这正是参照仓库栽的那件事）。
    # 拆成两段：规则照常工作，而按整串扫描的内容检查扫不到它。
    '已知口令样本'   = 'WangYP' + '666'
}

$files = Get-ChildItem -Recurse -File $out |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj)\\' -and
        # 不扫自己：本脚本里写着样本口令与正则本身，扫自己必然命中。
        $_.Name -ne 'assert-no-credentials.ps1'
    }

# 检查了**零个**对象与"检查通过"必须能区分开。
# 这条守卫来自一次真实的错误：一个文件枚举失败却零输出的循环，
# 被读成了"没有问题"（见 review/06）。
if ($files.Count -eq 0) {
    Write-Host '没有扫描到任何文件——检查等于没跑，不能当作通过。' -ForegroundColor Red
    exit 1
}

$hits = @()
foreach ($name in $patterns.Keys) {
    $matches = $files | Select-String -Pattern $patterns[$name] -List -ErrorAction SilentlyContinue
    foreach ($match in $matches) {
        $hits += [pscustomobject]@{
            Check = $name
            File  = $match.Path.Substring($out.Length + 1)
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Host ''
    Write-Host '模板生成物里出现了疑似凭据：' -ForegroundColor Red
    $hits | ForEach-Object { Write-Host ("  [{0}] {1}" -f $_.Check, $_.File) -ForegroundColor Red }
    Write-Host ''
    Write-Host '模板不得携带任何凭据。请把值移到环境变量或配置中心，仓库里只留占位符。' -ForegroundColor Red
    exit 1
}

Write-Host ("模板生成物干净：检查了 {0} 个文件，{1} 条规则，0 命中。" -f $files.Count, $patterns.Count) -ForegroundColor Green
exit 0
