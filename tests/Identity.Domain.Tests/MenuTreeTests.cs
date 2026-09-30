using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>
/// 菜单树聚合。核心不变量：物化路径由领域维护、移动必须同步改写全部后代、不能移进自己的子树。
/// </summary>
public sealed class MenuTreeTests
{
    private static MenuTree NewTree() => MenuTree.Create(new MenuTreeId(1));

    private static MenuId Id(long value) => new(value);

    private static MenuTitle Title(string value) => MenuTitle.Create(value).Value;

    [Fact]
    public void AddChild_ComputesPathFromParent()
    {
        // 参照仓库在 MenuService.cs:43 手工拼字符串：entity.IdSequences = $"{parent.IdSequences}{entity.Id}."
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("系统管理"));
        tree.AddChild(Id(1), Id(5), Title("用户管理"));
        tree.AddChild(Id(5), Id(12), Title("用户列表"));

        Assert.Equal("/1/", tree.Find(Id(1))!.Path.ToSequenceString());
        Assert.Equal("/1/5/", tree.Find(Id(5))!.Path.ToSequenceString());
        Assert.Equal("/1/5/12/", tree.Find(Id(12))!.Path.ToSequenceString());
        Assert.Equal(3, tree.Find(Id(12))!.Path.Depth);
    }

    [Fact]
    public void Move_RecomputesOwnAndAllDescendantPaths()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddRoot(Id(2), Title("B"));
        tree.AddChild(Id(1), Id(10), Title("A-1"));
        tree.AddChild(Id(10), Id(100), Title("A-1-1"));
        tree.AddChild(Id(100), Id(1000), Title("A-1-1-1"));

        Assert.True(tree.Move(Id(10), Id(2)).IsSuccess);

        // 自己与三代后代全部改写——这正是"整棵树是一个聚合"的理由。
        Assert.Equal("/2/10/", tree.Find(Id(10))!.Path.ToSequenceString());
        Assert.Equal("/2/10/100/", tree.Find(Id(100))!.Path.ToSequenceString());
        Assert.Equal("/2/10/100/1000/", tree.Find(Id(1000))!.Path.ToSequenceString());
        Assert.Equal(Id(2), tree.Find(Id(10))!.ParentId);
    }

    [Fact]
    public void Move_IntoOwnSubtree_IsRejected_BecauseItWouldCreateACycle()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddChild(Id(1), Id(10), Title("A-1"));
        tree.AddChild(Id(10), Id(100), Title("A-1-1"));

        var toSelf = tree.Move(Id(10), Id(10));
        var toDescendant = tree.Move(Id(10), Id(100));

        Assert.True(toSelf.IsFailure);
        Assert.Equal("identity.menu.move_into_own_subtree", toSelf.Error.Code);
        Assert.Equal("identity.menu.move_into_own_subtree", toDescendant.Error.Code);

        // 失败不能留下半改状态。
        Assert.Equal("/1/10/", tree.Find(Id(10))!.Path.ToSequenceString());
        Assert.Equal("/1/10/100/", tree.Find(Id(100))!.Path.ToSequenceString());
    }

    [Fact]
    public void Move_ToRoot_ResetsParentAndPath()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddChild(Id(1), Id(10), Title("A-1"));

        Assert.True(tree.Move(Id(10), null).IsSuccess);

        Assert.Null(tree.Find(Id(10))!.ParentId);
        Assert.Equal("/10/", tree.Find(Id(10))!.Path.ToSequenceString());
    }

    [Fact]
    public void Move_UnknownNodeOrParent_Fails()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));

        Assert.Equal("identity.menu.not_found", tree.Move(Id(99), null).Error.Code);
        Assert.Equal("identity.menu.not_found", tree.Move(Id(1), Id(99)).Error.Code);
    }

    [Fact]
    public void ChildrenOf_MatchesExactParentId_NotSubstring()
    {
        // 参照仓库用 LIKE '%{parentId}%' 找子节点（MenuService.cs:79）：
        // 父节点 1 会命中 12、21 的路径，而且前导通配符让索引失效。
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("父 1"));
        tree.AddRoot(Id(12), Title("父 12"));
        tree.AddChild(Id(1), Id(100), Title("1 的孩子"));
        tree.AddChild(Id(12), Id(120), Title("12 的孩子"));

        var childrenOfOne = tree.ChildrenOf(Id(1));

        Assert.Single(childrenOfOne);
        Assert.Equal(100L, childrenOfOne[0].Id.Value);
    }

    [Fact]
    public void DescendantsOf_ReturnsWholeSubtree_ExcludingSelf()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddChild(Id(1), Id(10), Title("A-1"));
        tree.AddChild(Id(10), Id(100), Title("A-1-1"));
        tree.AddRoot(Id(2), Title("B"));

        var descendants = tree.DescendantsOf(Id(1));

        Assert.Equal([10L, 100L], descendants.Select(static node => node.Id.Value).Order());
    }

    [Fact]
    public void Remove_NodeWithChildren_IsRejected()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddChild(Id(1), Id(10), Title("A-1"));

        var result = tree.Remove(Id(1));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.menu.has_children", result.Error.Code);
        Assert.NotNull(tree.Find(Id(1)));
    }

    [Fact]
    public void Remove_Leaf_Succeeds()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddChild(Id(1), Id(10), Title("A-1"));

        Assert.True(tree.Remove(Id(10)).IsSuccess);
        Assert.Null(tree.Find(Id(10)));
    }

    [Fact]
    public void Add_DuplicateId_IsRejected()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));

        var result = tree.AddRoot(Id(1), Title("又一个 A"));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.menu.already_exists", result.Error.Code);
    }

    [Fact]
    public void AddChild_UnknownParent_IsRejected()
    {
        var tree = NewTree();

        var result = tree.AddChild(Id(99), Id(1), Title("孤儿"));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.menu.not_found", result.Error.Code);
    }

    [Fact]
    public void Add_BeyondMaxDepth_IsRejected()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("L1"));
        var parent = Id(1);
        for (var depth = 2; depth <= MenuTree.MaxDepth; depth++)
        {
            var child = Id(depth * 10);
            Assert.True(tree.AddChild(parent, child, Title($"L{depth}")).IsSuccess);
            parent = child;
        }

        var tooDeep = tree.AddChild(parent, Id(9999), Title("太深了"));

        Assert.True(tooDeep.IsFailure);
        Assert.Equal("identity.menu.depth_exceeded", tooDeep.Error.Code);
    }

    [Fact]
    public void Move_BeyondMaxDepth_IsRejected_WithoutMutating()
    {
        // 深度必须在改动之前算出来；否则失败后还要回滚已经改过的路径。
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("深链根"));
        var parent = Id(1);
        for (var depth = 2; depth < MenuTree.MaxDepth; depth++)
        {
            var child = Id(depth * 10);
            tree.AddChild(parent, child, Title($"L{depth}"));
            parent = child;
        }

        tree.AddRoot(Id(2), Title("浅根"));
        tree.AddChild(Id(2), Id(200), Title("两层"));

        // 把「浅根」树移到深链末端会超过上限。
        var result = tree.Move(Id(2), parent);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.menu.depth_exceeded", result.Error.Code);
        Assert.Equal("/2/", tree.Find(Id(2))!.Path.ToSequenceString());
        Assert.Equal("/2/200/", tree.Find(Id(200))!.Path.ToSequenceString());
    }

    [Fact]
    public void Update_UnknownNode_Fails()
    {
        var tree = NewTree();

        Assert.Equal("identity.menu.not_found", tree.Update(Id(9), Title("X"), 0).Error.Code);
    }

    [Fact]
    public void Update_ReordersChildren()
    {
        var tree = NewTree();
        tree.AddRoot(Id(1), Title("A"));
        tree.AddChild(Id(1), Id(10), Title("后"), sortOrder: 5);
        tree.AddChild(Id(1), Id(11), Title("前"), sortOrder: 1);

        Assert.Equal([11L, 10L], tree.ChildrenOf(Id(1)).Select(static node => node.Id.Value));

        tree.Update(Id(10), Title("后"), sortOrder: 0);

        Assert.Equal([10L, 11L], tree.ChildrenOf(Id(1)).Select(static node => node.Id.Value));
    }
}
