# 手动 HTTP 旅程共同使用的响应解析；不输出令牌或业务正文。
function ConvertFrom-ApiResponse([string]$status, [string]$body) {
    $data = $null
    if ($status -in @('200', '201', '202')) {
        $envelope = $body | ConvertFrom-Json
        if ($envelope.success -ne $true) { throw '成功响应缺少统一信封。' }
        $data = $envelope.data
    }
    return @{ Status = $status; Body = $body; Data = $data }
}
