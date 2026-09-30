# 64 — 测试 schema 会残留，而且没有清理路径

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 19

> **用户在他的数据库客户端里看到 `nexusstack_platform` 下面有两个 `test_*` schema。**
> 而我此前报告过"跑完残留 schema 0 个"。

## 我错在哪

那句话是**在某一刻测的一次观测**，而我把它当成了一条**性质**写进了报告。
它与本仓反复出现的那一族是同一个形状：**验证覆盖了一个瞬间，结论说了一个规律。**

正确说法应该是："*这一次跑完*残留 0 个"——然后去问"进程被杀时会怎样"。

## 现场

```
test_4b0a684f7d9b4ade98a8c23748f58d1b   表数 1   →  probe_aggregate
test_657dcf712fb64694a3323147d8cd1ec9   表数 1   →  probe_aggregate
public                                  表数 0
```

**只有 `probe_aggregate`，没有 `outbox`/`inbox`**——这条线索说明它们创建于
"基座还没有那两张表"的时期，也就是本票早期的某次**中断**（构建失败后误用 `--no-build`、
或某次失败的构建周期）。

## 决定性实验：正常路径到底泄不泄漏

```
跑之前           残留 2
跑全量 451 条    残留 2
再单独跑三次     残留 2
```

**正常路径不泄漏** ✓。`await using` 在每个测试结束时都会 `DROP … CASCADE`。

**所以真正的缺口不是"泄漏"，而是"中断之后没有清理路径"**：
进程被杀时 `DisposeAsync` 根本不会执行，而残留**不会让任何测试变红**——
它只会让库慢慢变脏，直到有人在客户端里看见它（正如这次）。

## 修法：让残留**可识别**，并给它一个**主动**的清理入口

### 一、schema 名里带创建时刻

```
之前：test_<32 位 guid>
现在：test_<yyyyMMddHHmmss>_<8 位 hex>
```

随机段保证并发不撞；**时刻段是为了清理**——PostgreSQL 不记录 schema 的创建时间，
没有它就无法判断"多久算旧"。

### 二、`PostgresTestDatabase.CleanupStaleAsync(olderThan)`

- 名字带时刻的：早于 cutoff 的删掉
- 名字**对不上格式**的（更早版本留下的）：一律当过期
- 其余不动

### 三、一条测试，既验证清理、也执行清理

`StaleSchemas_AreCleanedUp_AndFreshOnesAreKept`：

1. 造一个"七天前被中断留下"的 schema
2. 同时开一个新鲜的
3. 调用清理
4. 断言：旧的没了、**新的还在**

把它放进 `dotnet test` 而不是写成脚本，是因为**它需要跑**——
一个放在 `scripts/` 里、没人记得执行的清理脚本，就是下一个"不会失败的检查"。

**实际效果**：这条测试第一次运行就把用户看到的那两个遗留清掉了。

```
跑完之后的残留：合计 0
```

## 反向验证

| 变异 | 结果 |
|---|---|
| `IsStale` 永远返回 false（清理变空操作） | `Assert.Contains() Failure: Item not found` **红** |
| 清理把新鲜的也删掉 | `Assert.DoesNotContain() Failure: Item found` **红** |

## 本条真正值得记的一句

**一次观测不是一条性质。** 说"残留 0 个"之前，应该先问：
*什么情况下它不会是 0？* 答案（进程被杀）本来是可以推出来的——
只是当时"测到了 0"让人停止了追问。
