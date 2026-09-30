using Xunit;

// **这个程序集里的测试不能并行跑。**
//
// 迁移里的 schema 名是编译进去的（`schema: "identity"`），所以这组测试操作的是
// **同一个真实的 `identity` schema**——每个用例开始前都要把它 DROP 掉重建以获得确定性。
// 两个类并行执行时，一个正在建、另一个刚把它删了，于是失败看起来像"映射写错了"。
//
// xUnit 默认**按类并行**，所以这条必须显式关掉。它与"每个测试一个临时 schema"
// 那套隔离是两种情形：那套用于常规集成测试，这套用于**迁移**——而迁移无法参数化 schema。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
