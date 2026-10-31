namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 三态值：未设置、设置为空、设置为具体值。
/// </summary>
/// <typeparam name="T">值的类型。</typeparam>
/// <remarks>
/// <para>
/// 名字不叫 <c>Optional</c>：那个词是部分语言的保留字（CA1716），
/// 而且这里的语义比"可选"更具体——它是"三种状态"，其中一态是"明确设成空"。
/// </para>
/// <para>工厂方法放在非泛型的 <see cref="OptionalValue" /> 上（泛型类型里不放静态成员，CA1000）。</para>
/// </remarks>
/// <remarks>
/// <para>
/// 样式覆盖必须能区分"<b>没有覆盖</b>"与"<b>覆盖成空值</b>"。
/// 字体族就是一个例子：<see langword="null"/> 在 <see cref="LyricStyle" /> 里表示
/// "用渲染后端的默认字体"，那是一个合法的覆盖目标——和"不覆盖"完全不是一回事。
/// 只用一个可空类型表达不了这两者，所以"不覆盖"用本类型表示，值本身仍然可以是
/// <see langword="null"/>。
/// </para>
/// <para>
/// 落进 JSON 时二者不会混淆：<b>键缺失</b>表示未设置，<b>键为 null</b> 表示设置为空
/// （docs/development.md §6，schema v2 接入时按此约定）。
/// </para>
/// </remarks>
public readonly record struct OptionalValue<T>
{
    private readonly bool _isSet;

    /// <summary>构造三态值。</summary>
    /// <param name="isSet">是否已设置。</param>
    /// <param name="value">值，允许为 <see langword="null" />。</param>
    /// <remarks>用 <c>internal</c> 而不是 <c>private</c>：工厂方法在非泛型的同程序集类型上。</remarks>
    internal OptionalValue(bool isSet, T? value)
    {
        _isSet = isSet;
        Value = value;
    }

    /// <summary>是否设置了值；值本身仍可能是 <see langword="null"/>。</summary>
    public bool IsSet => _isSet;

    /// <summary>设置的值；未设置时为 <see langword="default"/>。</summary>
    public T? Value { get; }

    /// <summary>取得值。</summary>
    /// <param name="value">设置的值；未设置时为 <see langword="default"/>。</param>
    /// <returns>是否已设置。</returns>
    public bool TryGet(out T? value)
    {
        value = Value;
        return _isSet;
    }

    /// <summary>把值隐式转成"已设置"。</summary>
    /// <param name="value">要设置的值。</param>
    /// <remarks>
    /// 这样写覆盖时可以直接写 <c>FontFamilyName = null</c>（显式覆盖成空）或
    /// <c>FontFamilyName = "某字体"</c>；<b>不写这个属性</b>才是"未覆盖"。
    /// </remarks>
    public static implicit operator OptionalValue<T>(T? value) => new(true, value);
}

/// <summary><see cref="OptionalValue{T}" /> 的工厂方法。</summary>
/// <remarks>
/// 放在非泛型类型上是为了满足 CA1000（泛型类型不声明静态成员），
/// 同时让调用处读起来更像一句话：<c>OptionalValue.Set(1.5f)</c>。
/// </remarks>
public static class OptionalValue
{
    /// <summary>未设置的值。</summary>
    /// <typeparam name="T">值的类型。</typeparam>
    /// <returns>未设置的三态值。</returns>
    public static OptionalValue<T> Unset<T>() => default;

    /// <summary>创建一个"已设置"的值。</summary>
    /// <typeparam name="T">值的类型。</typeparam>
    /// <param name="value">要设置的值，允许为 <see langword="null" />。</param>
    /// <returns>已设置的三态值。</returns>
    public static OptionalValue<T> Set<T>(T? value) => new(true, value);
}
