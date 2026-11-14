namespace MakoKaraGenerator.Scripting;

/// <summary>事件发射的显式锚点，不接受任意位置委托或原生对象。</summary>
public abstract record BurstAnchor
{
    private BurstAnchor() { }
    /// <summary>当前真实装载的基础元素 Advance 中心。</summary>
    /// <param name="Id">真实源元素身份。</param>
    public sealed record SourceToken(TokenId Id) : BurstAnchor;
    /// <summary>当前真实装载的整行 Advance 中心。</summary>
    /// <param name="Id">真实源行身份。</param>
    public sealed record SourceLine(LineId Id) : BurstAnchor;
    /// <summary>指定真实文字标记的 Advance 中心。</summary>
    /// <param name="Id">真实文字标记节点。</param>
    public sealed record TextMark(NodeId Id) : BurstAnchor;
    /// <summary>输出绑定内所有实际播放头；无人演唱是合法空集合。</summary>
    /// <param name="Mode">显式播放头锚点。</param>
    public sealed record BindingCursors(CursorAnchorMode Mode) : BurstAnchor;
    internal bool IsValid => this switch
    {
        SourceToken token => token.Id.IsValid, SourceLine line => line.Id.IsValid, TextMark mark => mark.Id.IsValid,
        BindingCursors cursors => Enum.IsDefined(cursors.Mode), _ => false,
    };
}
