using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Menus;

/// <summary>菜单标题。</summary>
public sealed class MenuTitle : ValueObject
{
    /// <summary>最大长度。</summary>
    public const int MaxLength = 64;

    private MenuTitle(string value) => Value = value;

    /// <summary>规范化后的标题。</summary>
    public string Value { get; }

    /// <summary>构造标题。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<MenuTitle> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<MenuTitle>(new Error("identity.menu_title.empty", "菜单标题不能为空。"));
        }

        return trimmed.Length > MaxLength
            ? Result.Failure<MenuTitle>(new Error("identity.menu_title.too_long", $"菜单标题不能超过 {MaxLength} 个字符。"))
            : Result.Success(new MenuTitle(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// 菜单节点。它是 <see cref="MenuTree"/> 聚合内部的<b>实体</b>，不是聚合根——
/// 它的物化路径只能由所属的树改写。
/// </summary>
public sealed class MenuNode : Entity<MenuId>
{
    internal MenuNode(MenuId id, MenuId? parentId, MenuPath path, MenuTitle title, int sortOrder)
        : base(id)
    {
        ParentId = parentId;
        Path = path;
        Title = title;
        SortOrder = sortOrder;
    }

    /// <summary>父节点标识；根节点为 <c>null</c>。</summary>
    public MenuId? ParentId { get; private set; }

    /// <summary>物化路径。<b>外部拿不到写入口</b>。</summary>
    public MenuPath Path { get; private set; }

    /// <summary>标题。</summary>
    public MenuTitle Title { get; private set; }

    /// <summary>同级排序。</summary>
    public int SortOrder { get; private set; }

    internal void Reparent(MenuId? parentId, MenuPath path)
    {
        ParentId = parentId;
        Path = path;
    }

    internal void Update(MenuTitle title, int sortOrder)
    {
        Title = title;
        SortOrder = sortOrder;
    }

    /// <summary>标题与排序是否**已经**是这两个值——空操作的判据属于节点自己。</summary>
    /// <param name="title">标题。</param>
    /// <param name="sortOrder">排序。</param>
    /// <returns>完全相同为 <c>true</c>。</returns>
    internal bool HasSameContentAs(MenuTitle title, int sortOrder) =>
        Title == title && SortOrder == sortOrder;
}

/// <summary>
/// 菜单树聚合根。
/// <para>
/// <b>为什么整棵树是一个聚合，而不是每个节点一个。</b>因为"移动一个节点"必须同时改写它所有后代的
/// 物化路径——如果每个节点是独立聚合，这个操作就跨了多个聚合，一次事务里的一致性只能靠调用方记得
/// 把后代也改一遍，而忘记改不会有任何报错，只会让树悄悄损坏。菜单树的规模是几十到几百个节点，
/// 整棵加载的代价可以接受。这个取舍记录在 <c>docs/adr/0001-menu-tree-is-one-aggregate.md</c>。
/// </para>
/// <para>
/// <b>路径用标识序列比较，不用 <c>LIKE</c>。</b>参照仓库的 <c>LIKE '%{parentId}%'</c>
/// （<c>MenuService.cs:79</c>）有两个独立缺陷：父节点 <c>1</c> 会命中 <c>12</c>、<c>21</c> 的路径；
/// 前导通配符让索引失效。
/// </para>
/// </summary>
public sealed class MenuTree : AuditedAggregateRoot<MenuTreeId>
{
    /// <summary>允许的最大层级（路径深度）。</summary>
    public const int MaxDepth = 8;

    private readonly List<MenuNode> _nodes = [];

    private MenuTree(MenuTree source) : base(source)
    {
        _nodes.AddRange(source._nodes.Select(node => new MenuNode(node.Id, node.ParentId, node.Path, node.Title, node.SortOrder)));
    }

    /// <summary>复制树及节点当前状态，不共享可变节点或待发布事件。</summary>
    /// <returns>独立快照。</returns>
    public MenuTree Snapshot() => new(this);

    private MenuTree(MenuTreeId id)
        : base(id)
    {
    }

    /// <summary>全部节点。</summary>
    public IReadOnlyList<MenuNode> Nodes => _nodes.AsReadOnly();

    /// <summary>创建一棵空树。</summary>
    /// <param name="id">树标识。</param>
    /// <returns>菜单树。</returns>
    public static MenuTree Create(MenuTreeId id) => new(id);

    /// <summary>查找节点。</summary>
    /// <param name="id">节点标识。</param>
    /// <returns>节点；不存在时为 <c>null</c>。</returns>
    public MenuNode? Find(MenuId id) => _nodes.Find(node => node.Id == id);

    /// <summary>某个父节点下的直接子节点，按排序值排列。<b>按标识精确匹配，不是模糊匹配。</b></summary>
    /// <param name="parentId">父节点标识；<c>null</c> 表示根节点。</param>
    /// <returns>子节点。</returns>
    public IReadOnlyList<MenuNode> ChildrenOf(MenuId? parentId) =>
        [.. _nodes.Where(node => node.ParentId == parentId).OrderBy(static node => node.SortOrder).ThenBy(static n => n.Id.Value)];

    /// <summary>某个节点的全部后代。</summary>
    /// <param name="id">节点标识。</param>
    /// <returns>后代节点。</returns>
    public IReadOnlyList<MenuNode> DescendantsOf(MenuId id)
    {
        var node = Find(id);
        return node is null ? [] : [.. _nodes.Where(other => node.Path.IsAncestorOf(other.Path))];
    }

    /// <summary>新增根节点。</summary>
    /// <param name="id">节点标识。</param>
    /// <param name="title">标题。</param>
    /// <param name="sortOrder">排序值。</param>
    /// <returns>新节点，或重叠/超深度的失败。</returns>
    public Result<MenuNode> AddRoot(MenuId id, MenuTitle title, int sortOrder = 0) =>
        Add(id, parentId: null, title, sortOrder);

    /// <summary>在指定父节点下新增子节点。</summary>
    /// <param name="parentId">父节点标识。</param>
    /// <param name="id">新节点标识。</param>
    /// <param name="title">标题。</param>
    /// <param name="sortOrder">排序值。</param>
    /// <returns>新节点，或父节点不存在/重叠/超深度的失败。</returns>
    public Result<MenuNode> AddChild(MenuId parentId, MenuId id, MenuTitle title, int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        return Add(id, parentId, title, sortOrder);
    }

    /// <summary>
    /// 移动节点（可连同整棵子树）到新父节点下，并同步改写全部后代的物化路径。
    /// </summary>
    /// <param name="nodeId">被移动的节点。</param>
    /// <param name="newParentId">新父节点；<c>null</c> 表示移到根。</param>
    /// <returns>成功，或节点不存在/产生环/超深度的失败。</returns>
    public Result Move(MenuId nodeId, MenuId? newParentId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        var node = Find(nodeId);
        if (node is null)
        {
            return Result.Failure(IdentityErrors.MenuNotFound(nodeId.Value));
        }

        MenuPath targetPath;
        if (newParentId is null)
        {
            targetPath = MenuPath.Root.Append(node.Id);
        }
        else
        {
            var newParent = Find(newParentId);
            if (newParent is null)
            {
                return Result.Failure(IdentityErrors.MenuNotFound(newParentId.Value));
            }

            // 移到自己的子树下会形成环——树会自我包含，遍历与授权推导都会失控。
            if (newParent.Id == node.Id || node.Path.IsAncestorOf(newParent.Path))
            {
                return Result.Failure(IdentityErrors.MenuMoveIntoOwnSubtree());
            }

            targetPath = newParent.Path.Append(node.Id);
        }

        if (node.Path.Equals(targetPath))
        {
            return Result.Success();
        }

        var oldPath = node.Path;

        // 深度必须在改动**之前**算出来：否则失败后还要回滚已经改过的路径。
        var subtreeHeight = _nodes
            .Where(other => oldPath.IsAncestorOf(other.Path))
            .Select(other => other.Path.Depth - oldPath.Depth)
            .DefaultIfEmpty(0)
            .Max();

        if (targetPath.Depth + subtreeHeight > MaxDepth)
        {
            return Result.Failure(IdentityErrors.MenuDepthExceeded(MaxDepth));
        }

        node.Reparent(newParentId, targetPath);

        foreach (var descendant in _nodes.Where(other => oldPath.IsAncestorOf(other.Path)))
        {
            descendant.Reparent(descendant.ParentId, descendant.Path.Rebase(oldPath, targetPath));
        }

        return Changed();
    }

    /// <summary>删除叶子节点。还有子节点的节点不允许删除。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <returns>成功，或节点不存在/仍有子节点。</returns>
    public Result Remove(MenuId nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        var node = Find(nodeId);
        if (node is null)
        {
            return Result.Failure(IdentityErrors.MenuNotFound(nodeId.Value));
        }

        var childCount = ChildrenOf(nodeId).Count;
        if (childCount > 0)
        {
            return Result.Failure(IdentityErrors.MenuHasChildren(childCount));
        }

        _nodes.Remove(node);
        return Changed();
    }

    /// <summary>更新节点的标题与排序。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="title">标题。</param>
    /// <param name="sortOrder">排序值。</param>
    /// <returns>成功，或节点不存在。</returns>
    public Result Update(MenuId nodeId, MenuTitle title, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(title);

        var node = Find(nodeId);
        if (node is null)
        {
            return Result.Failure(IdentityErrors.MenuNotFound(nodeId.Value));
        }

        // **空操作不是改变**（ADR-0011 的契约："Version 改变，当且仅当可观察状态改变了"）。
        //
        // 标题与排序都没动时提前返回：版本号不动，也就不会在一次什么都没改的"保存"里
        // 凭空制造出版本冲突——而误报冲突的代价是调用方开始重试、或者干脆忽略冲突，
        // 那时这个机制就废了，且废得很安静。
        if (node.HasSameContentAs(title, sortOrder))
        {
            return Result.Success();
        }

        node.Update(title, sortOrder);
        return Changed();
    }

    private Result<MenuNode> Add(MenuId id, MenuId? parentId, MenuTitle title, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(title);

        if (Find(id) is not null)
        {
            return Result.Failure<MenuNode>(IdentityErrors.MenuAlreadyExists(id.Value));
        }

        MenuPath path;
        if (parentId is null)
        {
            path = MenuPath.Root.Append(id);
        }
        else
        {
            var parent = Find(parentId);
            if (parent is null)
            {
                return Result.Failure<MenuNode>(IdentityErrors.MenuNotFound(parentId.Value));
            }

            path = parent.Path.Append(id);
        }

        if (path.Depth > MaxDepth)
        {
            return Result.Failure<MenuNode>(IdentityErrors.MenuDepthExceeded(MaxDepth));
        }

        var node = new MenuNode(id, parentId, path, title, sortOrder);
        _nodes.Add(node);
        return Changed(node);
    }
}
