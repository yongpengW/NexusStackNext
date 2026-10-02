# 浏览器可精确往返的 Int64 HTTP 契约

核验日期：2026-10-02。对应下一轮[票据 #44](https://github.com/yongpengW/NexusStackNext/issues/44)，依赖 #43。源码观察基于 `12f130b12837654c5a6470e892a0ed02895783ed` 及当前 Scheduling 评审修正工作树；本文不属于 #43 的交付。只读检查产品代码，并在系统临时目录运行无依赖的 System.Text.Json / Node 探针；没有修改产品、连接数据库或安装包。本文是研究建议，尚不是 ADR，也不代表真实 HTTP 验收已通过。

**建议：在现有 `AddApiResponseContract` 的 HTTP JSON 选项里注册一个只处理 `long` 的转换器，所有 Int64 响应始终写十进制字符串，输入兼容字符串和精确整数数字；同时按输入/输出方向修正 OpenAPI。** 保留领域、数据库、JWT、RabbitMQ、Outbox、Redis 内部序列化。不要把全局 `JsonNumberHandling` 设为 `WriteAsString`，也不要仅给 `long` 的 `JsonTypeInfo` 加 NumberHandling 后就宣告完成。

## 1. 缺口与现有缝

- NS 与 PoS 的 `Infrastructure/Converters/JsonLongConverter.cs` 都继承 `JsonConverter<long>`：字符串输入走 `Convert.ToInt64`，数字输入走 `reader.GetInt64()`；两个 Write 分支均写字符串，实际上与大小无关。两者 `Domain/*Core/ServiceCollectionExtensions.cs` 分别在 242、548 行注册 MVC 转换器；各自 `Infrastructure/Options/JsonOptions.cs` 还提供带转换器的独立选项。PoS 另外用于 SignalR；NSN 此票不涉及该内部协议。
- NSN 四个 HTTP 宿主 Platform、Gateway、Costing、Pricing 均显式调用 `AddApiResponseContract` 和 `AddOpenApi`，后者版本锁定为 **10.0.12**。共享缝在 [ApiResponseExtensions](../../src/BuildingBlocks/BuildingBlocks.Web/ApiResponseExtensions.cs)，当前没有长整数设置；都使用 Minimal APIs，不应只配置 MVC `AddJsonOptions`。
- [ApiResponses](../../src/BuildingBlocks/BuildingBlocks.Web/ApiResponses.cs) 返回 TypedResults，成功与错误 timestamp 均为 long；[ApiPage](../../src/BuildingBlocks/BuildingBlocks.Web/ApiPage.cs) 的 total、totalPage 为 long，page、limit 为 int；[ApiProblemDetailsWriter](../../src/BuildingBlocks/BuildingBlocks.Web/ApiProblemDetailsWriter.cs) 的 WriteAsJsonAsync 使用宿主 HTTP 选项。这些都是同一改动的读者。
- [OpenApiAggregation](../../src/Gateway/NexusStackNext.Gateway/OpenApiAggregation.cs) 对来源文档深拷贝、改 `$ref` 前缀、合并，**另用 `JsonSerializerOptions.Web.GetJsonSchemaAsNode(typeof(ApiProblemDetails))` 生成 EdgeProblem**。只加宿主 OpenAPI transformer 会漏掉后者的 timestamp。需让边缘独立错误 schema 也来自同一 HTTP 契约，并验证来源失效时仍正确。

公开字段必须按声明的 Int64 类型覆盖，不按属性名猜测：Identity 的 userId / roleId / menuId / apiResourceId、可空 parentMenuId / menuId 与菜单集合；Files 的 fileId / size；Platform 的 version / expectedVersion（DELETE 在 query）；Scheduling 的 taskId / version / triggerSequence；Auditing 的 id 与嵌套 fact.subjectVersion；Costing / Pricing 的 version / inputRevision / calculatedRevision / costingRevision / epoch / expectedEpoch / expectedVersion、尝试历史和计划接受回执中的 planId / triggerSequence。Guid taskId / occurrenceId 保持 Guid 字符串，金额 decimal 保持当前数字语义。

## 2. 为什么必须改变线格式

RFC 8259 §6 允许实现限制数字精度，跨常见 binary64 实现能一致精确表达的整数范围是 `[-(2^53)+1, (2^53)-1]`。因此 JSON 文本合法且 .NET 能 GetInt64，并不代表 JavaScript 可无损接收。[RFC 8259 §6](https://www.rfc-editor.org/rfc/rfc8259#section-6)

ECMAScript 的 Number 安全整数上限为 `2^53-1`；普通 JSON.parse 返回 Number，不自动返回 BigInt。保持字符串可直接 JSON.stringify 回传，客户端若需要整数运算可显式 BigInt，再以字符串传输。[Number.MAX_SAFE_INTEGER](https://tc39.es/ecma262/multipage/numbers-and-dates.html#sec-number.max_safe_integer)、[JSON.parse](https://tc39.es/ecma262/multipage/structured-data.html#sec-json.parse)

本机 Node 实测：`JSON.parse('{"id":9007199254740993}').id` 为 `9007199254740992`；字符串形式解析、stringify、再次解析仍严格等于 `"9007199254740993"`。这是客户端失真的可复现实验，尚不是 NSN 的端到端验收。

## 3. 方案比较与输入规则

| 方案 | 影响与结论 |
|---|---|
| 全局 NumberHandling = WriteAsString + AllowReadingFromString | 改动小但同时改变 int、decimal、double 等；违背 #44 的不误改约束，不选。 |
| HTTP 专用 JsonConverter<long> | 保持 CLR 模型；覆盖属性、匿名结果、集合元素，Nullable<long> 由框架包装；明确异常规则；推荐。 |
| 仅修改 long / long? 的 JsonTypeInfo.NumberHandling | 看起来更少代码，但本机 .NET 10 探针中普通属性变字符串、long[] / long?[] 元素仍输出数字；单独使用不满足集合判据。 |
| 每个 DTO 把 ID 改为 string | 单个端点直观，但本轮还涉及版本、分页、timestamp、嵌套 DTO、匿名结果与多个宿主；会留下重复映射和遗漏。领域/应用契约不应为浏览器改型。 |

NumberHandling 的读字符串与写字符串是独立标志；适用到所有数字的选项不能当作 Int64 专用开关。自定义转换器是 System.Text.Json 官方扩展点，Nullable 包装和字典键处理也有专门规则。[NumberHandling](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonnumberhandling?view=net-10.0)、[转换器](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/converters-how-to)

建议线格式和兼容范围：

1. 输出：所有 long，包括 0、1、timestamp、total，始终 invariant 十进制字符串；long? 为字符串或 JSON null。不能小值数字、大值字符串。
2. 数字输入：用 `Utf8JsonReader.TryGetInt64` 读取原始整数 token，不经过 double/decimal 中转。接受 Int64 全范围，包括已经由精确客户端发来的 `9007199254740993`；服务器无法修复浏览器在发请求前已经舍入的值。
3. 字符串输入：明确选择 invariant、可选正负号、ASCII 十进制数字、Int64 范围；兼容当前 STJ 的 `"+1"`、`"01"`，输出归一为 `"1"`。不接收空白、空串、小数、科学计数、越界或布尔/对象。若决定只接收规范数字串，应明确记为兼容性收紧，不要无声改变。
4. 数字 `1.0` / `1e3` 虽然数学上是整数，现有 STJ Int64 路径不接收，保留这一词法规则；失败统一抛 JsonException，使既有 Minimal API/ProblemDetails 链处理为 400，不能让 FormatException 或 OverflowException 逃成 500。
5. 对 long 非空值收到 null 拒绝；long? 的 null 保留。若支持 Dictionary<long, T>，转换器显式实现 ReadAsPropertyName / WriteAsPropertyName，否则新增转换器可能破坏框架原有数字字典键支持。现有公开 DTO 未发现该形状，但 long 集合和匿名嵌套必须作为边界探针。

官方 Int64Converter 源码直接读取 Int64，按选项选择带引号读写；不存在先转 double 的步骤。[.NET 10.0.12 Int64Converter](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Value/Int64Converter.cs)、[TryGetInt64](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.utf8jsonreader.trygetint64?view=net-10.0)

临时探针 `nsn-int64-research-probe.cs` 的 NumberHandling 结果：id、nested.version 为字符串，items `[1,9223372036854775807]` 和 nullableItems `[null,-9223372036854775808]` 仍为数字；page=7、amount=1.25 不变。该探针成功退出；临时 file-app 有裁剪/AOT 提示，不是仓库 build 验收。仅设置类型元数据不能替代公开 HTTP 集合测试。类型级自定义虽为受支持扩展点，属性与集合的有效 NumberHandling 传播仍需用事实验证。[自定义合同](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/custom-contracts)、[JsonPropertyInfo 源码](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/JsonPropertyInfo.cs)

路由与查询参数不走 JSON 转换器。保留 `{id:long}` 约束和 CLR long 绑定，前端以拿到的字符串插入 URL；query expectedVersion 同理，不能 Number(id) 后再拼接。无效 query 通常是绑定 400；不匹配 `{id:long}` 的路径是路由 404，这与 body 400 是不同边界，避免误改既有状态语义。[Minimal API 绑定](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0)

## 4. OpenAPI 10.0.12 的具体实现约束

内置 OpenAPI 使用 HTTP JsonOptions 的元数据；Web 默认 AllowReadingFromString 会把数字 schema 表示为数字/字符串联合。自定义转换器不能靠 schema 推导自动知道它的线格式，因此必须显式 transformer。[OpenAPI 类型元数据](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0#numeric-types)、[schema exporter](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema)

建议只变 long / long? 的 schema，保留其他类型既有行为：

- 响应：`type: string`，规范十进制 pattern、描述中的有符号 64 位范围和示例字符串；nullable 添加 null。不要保留会误导生成器的纯 `integer/int64` 响应。
- JSON body 输入：`oneOf` 的字符串整数分支 + `integer/format:int64` 数字分支，数字分支 minimum/maximum 是 Int64 精确上下界；nullable 再加 null。字符串分支描述同一 Int64 范围，pattern 与实际 Read 接受的符号/前导零规则一致。JSON Schema 的 integer 语义会接受数学整数表达如 1e3，描述需说明绑定要求整数字面量；不能宣称 schema 单靠 integer 就表达了全部词法规则。
- path / query：保留原绑定方式；为客户端生成准确精度类型，公开 schema 可使用十进制字符串及范围说明。不能把参数 schema 的 string 理解为 URL 中需要 JSON 引号。按 `ParameterDescription.Source` 区分 JSON body 与 route/query。
- transformer 先清理旧的 type / format / pattern / anyOf / oneOf 等冲突结构，再形成目标表达，保留属性描述、必填与 null 语义。

`OpenApiSchemaTransformerContext.ParameterDescription` 在响应为 null，在请求携带 ApiParameterDescription；框架将它传给整个嵌套 schema 遍历，所以可以在 long 属性、集合 item 层判断方向。schema transformer 在 operation / document transformer 前执行。[上下文源码](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/OpenApi/src/Transformers/OpenApiSchemaTransformerContext.cs)、[转换顺序](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/customize-openapi?view=aspnetcore-10.0)

**需防方向被复用抹掉。** 10.0.12 源码每次创建并转换 schema，但组件 ID 按 CLR 类型命名，AddComponent 已有同名时保留先入者。同一个 DTO 或嵌套类同时用于请求和响应时，方向不同不保证得到不同组件；一个方向的联合类型可能污染另一个方向。当前主要 long-bearing 请求/响应类型分离，不应据此省掉共享 DTO 的探针。可对出现此情况的形状采用独立请求/响应 DTO 或显式内联该 schema；若要做普遍的方向组件拆分，先验证生成文档和 `$ref`，避免依赖内部 `x-schema-id` 实现。不要未经验证给所有 schema 内联造成不必要的文档膨胀。[SchemaService](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/OpenApi/src/Services/Schemas/OpenApiSchemaService.cs)、[组件注册](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/OpenApi/src/Extensions/OpenApiDocumentExtensions.cs)

边缘聚合的 EdgeProblem 是独立生成路径：可复用配置后的 HTTP options + 统一 schema 变换，或复用网关来源文档中已经正确的错误 schema，并保留取源失败时正确的后备定义。测试应同时看 Platform / Costing / Pricing 的源文档、gateway 自身文档，以及实际聚合文档，不能只验证 transformer 函数。

## 5. 按“它读什么”列出契约迁移对象

此快照 `rg GetInt64 tests -g '*.cs'` 共 88 处：86 处为 HTTP JsonElement 读者，另 2 处为 NpgsqlDataReader。不要对全仓盲目替换。

| 读者 | 受影响的对象与处理 |
|---|---|
| `HostIntegration.Tests/ApiResponseContractTests.cs`（9） | total、totalPage、taskId、userId、timestamp；改为先断言 JSON string 再 invariant 解析；page/limit/code/status 的 GetInt32 保留。 |
| `ApiTransportTests.cs`（1）、`PrivateFilesAccessTests.cs`（6）、`FilesPersistenceJourneyTests.cs`（7） | fileId、size 变字符串；流下载与 range/ETag/字节断言不变。 |
| `AuditAccessTests.cs`（3）、`AuthorizationChainJourneyTests.cs`（3）、`IdentityPersistenceJourneyTests.cs`（7）、`PlatformSettingsAccessTests.cs`（3） | userId、menuId、roleId 与嵌套菜单 ID；字符串插入路由无需 Number 转换。 |
| `AuditBusinessJourneyTests.cs`（5）、`PlatformPersistenceJourneyTests.cs`（6） | subjectVersion、version、分页 total；排序/版本加法必须显式按 Int64 解析，不进行字符串词典排序。 |
| `SchedulingAccessTests.cs`（3）、`SchedulingDefinitionTests.cs`（4）、`SchedulingDeliveryJourneyTests.cs`（3）、`SchedulingOccurrenceTests.cs`（9）、`SchedulingPersistenceJourneyTests.cs`（4） | taskId、version、triggerSequence；HTTP 返回 string 的断言与数字请求兼容测试分开。 |
| `ScheduledCostBusinessJourneyTests.cs`（7）、`Costing.IntegrationTests/BusinessCooperationTests.cs`（10） | version、costingRevision、taskId；跨进程业务链保留原有语义，改读字符串。 |
| `Pricing.IntegrationTests/PricingCacheTests.cs` | Deserialize<PriceQuoteView>(JsonSerializerOptions.Web) 本身可读字符串数字；需继续验证缓存内部格式未改变，不能因此代替线格式断言。 |
| `HostIntegration.Tests/ApiTestResponse.cs` | 只取 data，不强制数字；可加入严格的 HTTP Int64 读取助手供上面读者使用，助手必须拒绝数字输出，避免测试同时容忍新旧响应而漏掉回归。 |
| `Identity.IntegrationTests/IdentityUseCasePersistenceTests.cs`（2） | NpgsqlDataReader.GetInt64，属于数据库验证，必须保留。 |
| `scripts/verify-user-journey.ps1` | 从 ConvertFrom-Json 得到 fileId/userId/menuId/roleId 后插入 URL；字符串本来可用，应补准确类型/往返断言，不能额外转 [double]/[int]。 |
| `scripts/verify-write-paths.ps1` | 当前只读登录、value、lastRunAt，不依赖数字 ID；仍要回归，但没有理由修改它的调度逻辑。 |
| `docs/adr/0018-http-response-contract.md` | 成功/错误示例 timestamp 是数字，分页说明也默认数字；新 ADR 说明 supersede 的字段格式，保留原决定历史并提供迁移说明。 |
| `docs/durable-scheduling.md`、`docs/pricing-recalculation.md`、`docs/platform-settings.md`、`docs/costing-pricing-cooperation.md`、`docs/private-files.md` | expectedVersion/expectedEpoch 请求示例与返回 ID/size 说明；新示例优先字符串，明确旧的精确数字请求仍可接受。 |
| OpenAPI schema / aggregate tests | 当前只验证属性存在，缺少 string-vs-integer 断言；新增方向、nullable、引用可解析和 EdgeProblem timestamp 校验。 |

当前没有前端应用代码或 JavaScript API 客户端。不要把 PowerShell ConvertFrom-Json 或 .NET GetInt64 的成功当作浏览器验收。CI 尚无显式 setup-node；若测试驱动 Node，应声明工具依赖并在 Linux CI 安装步骤中固定版本，而不是假设 runner 永远自带。

## 6. 最小验证链与边界

1. 公共 HTTP 先红：真实网关注册/登录取得大于 2^53 的业务 ID，由 Node `JSON.parse` 读取，确认 typeof 为 string，直接串入后续真实授权/文件/计划端点或 JSON.stringify body，必须命中原对象。覆盖别名 ID 取错、伪版本冲突这两个用户可见后果；不能只验证序列化文本。
2. 用正常测试宿主的公开探针接口覆盖 `long.MinValue`、`long.MaxValue`、2^53 两侧、nullable、数组、匿名嵌套、分页与成功/错误 timestamp；业务端点覆盖 nullable parentMenuId/menuId 与实际 CAS。不存在能自然达到 long.MaxValue 版本的生产流程，不要新增生产调试端点或篡改生产表。
3. 字符串与精确数字输入均成功；越界、非整数、错误 token、非法字符串均 400 ProblemDetails，并检查写入/版本未发生。nullable null 与非空 null 分开。整数非法 query 和 route 的状态按绑定/路由边界分别断言。
4. Int32 code/page/limit、decimal 金额、double intervalSeconds、Guid、null 与文件流语义保持不变。已有测试承担行为回归，新增断言只补真实缺口。
5. OpenAPI 请求与响应分别验证，加入复用同一个 DTO 的 schema 探针；四宿主及网关聚合都检查到非空对象集。验证 `$ref` 指向正确 source 前缀和正确方向，尤其 EdgeProblem 独立生成。
6. 默认独立 SystemTextJsonIntegrationEventSerializer、JWT claims 与 Redis cache 路径不读取 HTTP options；对至少一条现有含 long 的消息做线格式不变的协议验证。保持旧消息能重放，不改数据库迁移。

这份研究只完成设计证据收集。实施仍须按仓库流程完成 TDD、构建 → 串行测试 → 格式、凭据/票据/模板检查、Linux CI、Standards / Spec 双轴评审，再合并 #44。
