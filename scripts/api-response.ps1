# 手动 HTTP 旅程共同使用的响应解析；不输出令牌或业务正文。
function Assert-ApiInt64([object]$value, [string]$field) {
    $parsed = 0L
    if ($value -isnot [string] -or $value -notmatch '^-?(0|[1-9][0-9]*)$' -or
        -not [long]::TryParse($value, [Globalization.NumberStyles]::AllowLeadingSign,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        throw "$field 必须是精确的 Int64 十进制字符串。"
    }
}

function ConvertFrom-ApiResponse([string]$status, [string]$body) {
    $data = $null
    if ($status -in @('200', '201', '202')) {
        $envelope = $body | ConvertFrom-Json
        if ($envelope.success -ne $true) { throw '成功响应缺少统一信封。' }
        Assert-ApiInt64 $envelope.timestamp 'timestamp'
        $data = $envelope.data
    }
    return @{ Status = $status; Body = $body; Data = $data }
}
