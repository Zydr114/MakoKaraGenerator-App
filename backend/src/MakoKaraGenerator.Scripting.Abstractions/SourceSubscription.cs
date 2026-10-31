using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>源事件订阅，独立于本帧的输出绑定，不允许帧内扩大。</summary>
public abstract record SourceSubscription
{
    private SourceSubscription() { }
    /// <summary>固定绑定所关联的真实源／装载／节点；不是当前可见集合。</summary>
    public sealed record BoundSources : SourceSubscription;
    /// <summary>整个固定工程的时间线。</summary>
    public sealed record Project : SourceSubscription;
    /// <summary>指定源分支；在构建固定会话时验证存在。</summary>
    public sealed record Branch : SourceSubscription
    {
        /// <summary>指定真实非空分支身份。</summary>
        public Branch(BranchId id)
        {
            if (!id.IsValid) throw new ArgumentException("订阅分支身份无效。", nameof(id)); Id = id;
        }
        /// <summary>固定订阅分支。</summary>
        public BranchId Id { get; }
    }
}

/// <summary>源历史查询的封闭筛选；不以显示节点冒充源 ID。</summary>
public abstract record SourceFilter
{
    private SourceFilter() { }
    /// <summary>查询完整工程的真实源起唱。</summary>
    public sealed record Project : SourceFilter;
    /// <summary>查询一个源分支。</summary>
    public sealed record Branch : SourceFilter
    {
        /// <summary>指定有效分支。</summary>
        public Branch(BranchId id)
        {
            if (!id.IsValid) throw new ArgumentException("筛选分支无效。", nameof(id)); Id = id;
        }
        /// <summary>真实源分支。</summary>
        public BranchId Id { get; }
    }
    /// <summary>查询指定源行。</summary>
    public sealed record Lines : SourceFilter
    {
        /// <summary>指定非空、去重、真实行身份；不悄悄去重。</summary>
        public Lines(ImmutableArray<LineId> ids)
        {
            if (ids.IsDefaultOrEmpty || ids.Any(static id => !id.IsValid) || ids.Distinct().Count() != ids.Length)
                throw new ArgumentException("筛选行身份必须有效、非空且不重复。", nameof(ids)); Ids = ids;
        }
        /// <summary>固定源行集合。</summary>
        public ImmutableArray<LineId> Ids { get; }
    }
    /// <summary>查询指定真实源元素。</summary>
    public sealed record Tokens : SourceFilter
    {
        /// <summary>指定非空、去重、真实元素身份。</summary>
        public Tokens(ImmutableArray<TokenId> ids)
        {
            if (ids.IsDefaultOrEmpty || ids.Any(static id => !id.IsValid) || ids.Distinct().Count() != ids.Length)
                throw new ArgumentException("筛选元素身份必须有效、非空且不重复。", nameof(ids)); Ids = ids;
        }
        /// <summary>固定源元素集合。</summary>
        public ImmutableArray<TokenId> Ids { get; }
    }
}
