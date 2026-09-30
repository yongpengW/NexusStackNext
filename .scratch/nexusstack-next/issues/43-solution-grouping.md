# 43 — 解决方案分组：让 VS 里看得清

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 42

> 用户打开 VS 反馈：37 个项目平铺，没有文件夹分组，可读性差。

## 关于我早先那个结论：它是**对的**，而我一度错怪了它

票据 01 那会儿我试过 `<Folder>` 分组，遇到"项目被静默丢掉"，于是得出结论
**"嵌套 Folder 不被解析"**，改回扁平列表。

重做时我先后给出过两个说法，**两个都错**，值得完整记下来：

**第一次**：我说"那个结论是错的"，理由是"漏项目在还原阶段会报 MSB3202，所以不可能是 Folder 造成的"。
——这个推理偷换了问题：MSB3202 只在**路径不存在**时触发，而静默丢项目是另一回事。

**第二次**：我做了个探针，把 `<Folder>` 套 `<Folder>`，得到"项目数 0"，据此更确信结论错了。
——**探针里混入了第二个变量**：我给那个探针写的名字是 `Name="probe-src"`，少了首尾斜杠。
错误信息其实说得很清楚：

```
Solution folder path 'probe-src' must start and end with '/'.
```

**把变量分开之后，两个事实都确立了：**

| 现象 | 结果 |
|---|---|
| `<Folder>` 套 `<Folder>`（名字都规范） | 项目数 **37 → 35**，被套住的**两个项目被静默忽略**；`sln list` 不报错，`build` 也成功 |
| 扁平 `<Folder>`，名字少尾斜杠 | 项目数 **0**，`MSB4025` 响亮报错 |

**所以票据 01 的结论是正确的**，只是原因写得不够准：不是"Folder 不被解析"，
而是**"嵌套的那一层里的项目会被静默吞掉"**。

**教训与这个项目里反复出现的是同一条**：我在同一个问题上连续两次下了未经核实的结论，
而两次都是**我自己的推理**而非**实测**给出的答案。探针便宜，猜不便宜。

## 现在的分组

镜像磁盘布局。`<Folder Name="/src/Services/Identity/">` 这种**路径式名字**由 VS 展开成层级
（与参照仓库 `<Folder Name="/Domain/">` 同一写法）。

```
src/BuildingBlocks/          3 个
src/Services/Identity/       4 个   ← 五个上下文各一个分组，各含 Domain/Application/Infrastructure/Api
src/Services/Platform/       4 个
src/Services/Scheduling/     4 个
src/Services/Auditing/       4 个
src/Services/Files/          4 个
src/Gateway/                 2 个
tests/                      12 个
Solution Items/              7 个根文件（README / AGENTS / CONTEXT-MAP / 三个构建基线 / .editorconfig）
```

## 一个不对称，值得记下来

`<Project>` 会被还原阶段校验，`<File>` **不会**。后果是完全反向的：

- 解决方案漏一个项目 → **有人会立刻知道**（构建失败）
- `Solution Items` 指向一个改名后的文件 → **没有任何东西会响**，VS 里那个条目只是不出现

所以给 `check-tracker.ps1` 加了第 9 项：`<File Path>` 必须指向真实存在的文件。
**反向验证**：把 `CONTEXT-MAP.md` 改成不存在的名字 → 精确命中；还原后恢复干净。

## 验证

| 检查 | 结果 |
|---|---|
| 解决方案项目数 | **37** |
| 与磁盘 csproj **双向**对照 | 无遗漏、无多余 |
| `dotnet build` | 0 警告 0 错误 |
| `dotnet test` | **392/392 全绿** |
| 模板生成物 | 37 个项目、0 名字残留、**分组与 Solution Items 都保留**、构建 0 警告 0 错误 |

## 我没能验证的（说清楚）

**VS 里那棵树的实际样子。** 我能证明的是：项目一个没少、条目指向的文件都存在、
SDK 解析这份文件不报错、模板生成物也一致。

**分组在 Solution Explorer 里渲染成什么样，只有你打开才知道。** 如果层级不是你想要的
（比如 `src > Services > Identity` 太深、或者希望 `tests` 也按上下文分组），告诉我怎么改。

## 产出

- `NexusStackNext.slnx`：扁平列表 → 分组
- `scripts/check-tracker.ps1`：新增 `<File>` 存在性检查（第 9 项）
