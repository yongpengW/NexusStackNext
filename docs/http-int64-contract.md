# HTTP Int64 契约与客户端迁移

对应 [#44](https://github.com/yongpengW/NexusStackNext/issues/44)、[ADR-0021](adr/0021-http-int64-decimal-strings.md)。

| 声明类型 / 用途 | HTTP JSON 输出 |
|---|---|
| long：Identity ID、文件 ID/size、计划 ID/triggerSequence、version/epoch/revision、timestamp、total/totalPage | 十进制字符串，包括 `"0"` 和 `"1"` |
| long?：parentMenuId、menuId 等 | 同上，或 JSON null |
| 数组、字典值、嵌套 DTO、匿名对象里的 long | 同一规则；按 CLR 类型，绝不猜字段名 |
| int：code/status/page/limit、次数 | 数字 |
| decimal 金额、double 间隔 | 保留既有数字语义；不扩展为金融十进制客户端方案 |
| Guid / 日期 / 文件下载 | 既有格式 / 字节流 |

示例（省略 message）：

```json
{"success":true,"code":200,"data":{"taskId":"9007199254740993","version":"1","parentMenuId":null},"timestamp":"1790899200000","traceId":"..."}
```

JSON body 输入接受 Int64 范围 `-9223372036854775808` 至 `9223372036854775807`：
字符串由 ASCII 十进制数字组成，允许一个正负号和前导零（`"+01"` 归一输出为 `"1"`）；
也兼容不经浮点中转的整数数字 token。空白、空串、尾随空字符、越界、小数、指数、布尔、数组或对象
返回 400 ProblemDetails，绑定失败不会执行端点。非空 long 不接受 null，long? 接受 null。
`1.0` 与 `1e3` 虽在数学上可能为整数，仍不属于这里接受的整数 token。

路径与查询字符串继续使用原有 ASP.NET long 绑定，URL 中不加 JSON 引号。
不匹配 `{id:long}` 约束的路径仍可能是 404；非法 query 是绑定 400，两者是不同边界。
OpenAPI Int64 输出为 string / int64-decimal，JSON 输入为 string / integer 的 oneOf，nullable 另含 null。
范围与数字词法约束写在说明里，JSON Schema 的 integer 自身不能限制指数写法。

## JavaScript / TypeScript

```typescript
type Plan = { taskId: string; version: string };
const page = await fetch('/api/scheduling/tasks').then(r => r.json());
const plan: Plan = page.data[0];
await fetch(`/api/scheduling/tasks/${plan.taskId}/pause`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ expectedVersion: plan.version }),
});
// 如需整数运算：BigInt(plan.version) + 1n；发 JSON 前用 .toString()。
```

移除 ID/版本上的 Number(...)、parseInt、一元 + 及浮点运算；重新生成 OpenAPI 客户端。
页面页码仍是 int。Date(timestamp) 需显式转换已知安全范围内的 Unix 毫秒值；这不适用于雪花 ID。
.NET Web JSON 默认可读取数字字符串；严格 JsonElement 调用者应先读字符串再 invariant 解析。
精确数字请求仍兼容，但浏览器已舍入的数字无法由服务端恢复。
领域、数据库、JWT、RabbitMQ、Outbox 与 Redis 内部序列化不采用 HTTP 选项。

## 验证

HostIntegration.Tests 需要 PATH 中有 Node.js 24，CI 显式安装该主版本。
真实 HTTP + Node JSON.parse/stringify 验证大整数边界；真实网关验证暂停实际计划及旧版本冲突。
共享/递归 DTO、所有宿主及聚合 OpenAPI、来源离线的 EdgeProblem 均有断言。
既有 HTTP 测试使用 ReadHttpInt64，拒绝数字响应；数据库和 OpenAPI 数字范围仍用 GetInt64。
执行 `pwsh scripts/run-tests.ps1`，不要并行运行解决方案测试。
