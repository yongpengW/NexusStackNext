# HTTP 的 Int64 始终以十进制字符串返回

NS / PoS 的 long 转换器避免了 JavaScript Number 对雪花 ID 的舍入，NSN 也必须保留这项能力。
所有宿主经 `AddApiResponseContract` 在 HTTP JSON 边界注册专用转换器：包括小值在内的全部
Int64 输出固定为 invariant 十进制字符串，nullable 保留 null；输入兼容十进制字符串与精确整数 token。
这替代 ADR-0018 示例中 timestamp 的数字格式，信封、状态码、分页和文件流决定继续有效。

领域、数据库和应用 DTO 仍使用 long，JWT、RabbitMQ、Outbox、Redis 的内部序列化保留原协议。
不选全局 `WriteAsString`，因为它还会改变 int、decimal、double；不逐个端点转换 ID，
因为版本、分页、时间戳、匿名嵌套和数组同样会越过这个边界。

OpenAPI 先统一描述字符串输出，再为含 Int64 的 JSON 输入引用图投影独立 schema，声明字符串或
整数输入。共享及递归 DTO 保持引用，不把输入兼容性传播到输出，也不内联所有响应。
网关独立生成的 EdgeProblem 使用相同格式，即使来源不可用也准确。
`int64-decimal` 是字符串格式提示；客户端应以 string 保存，用 BigInt 计算后再转 string 回传。

这是 HTTP 输出类型的兼容性变化，已有客户端需要更新模型及生成代码。服务器无法修复客户端
在发出请求之前已舍入的值。完整规则、迁移例子和验收见 [HTTP Int64 契约](../http-int64-contract.md)。
