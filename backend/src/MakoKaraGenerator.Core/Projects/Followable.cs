namespace MakoKaraGenerator.Core.Projects;

/// <summary>已唱外观中可以「跟随未唱」的一项：跟随，或显式取值（值本身可以是关闭）。</summary>
/// <typeparam name="T">值的类型。</typeparam>
/// <remarks>
/// 默认值是「跟随」（D-P3-6）。「跟随」与「显式关闭」不同：前者随未唱一起变，后者固定关闭。
/// 跟随只在样式解析的最后一步展开（<see cref="Resolve"/>），各层覆盖之间保持跟随，
/// 这样下层只改未唱时，已唱仍会跟着变。
/// </remarks>
public readonly record struct Followable<T>
{
    private readonly bool _isSet;

    internal Followable(bool isSet, T? value)
    {
        _isSet = isSet;
        Value = isSet ? value : default;
    }

    /// <summary>是否跟随未唱。</summary>
    public bool FollowsUnsung => !_isSet;

    /// <summary>显式的值；跟随时为 <see langword="default"/>。</summary>
    public T? Value { get; }

    /// <summary>展开跟随。</summary>
    /// <param name="unsung">未唱一侧的值。</param>
    /// <returns>跟随时返回未唱一侧的值，否则返回显式值。</returns>
    public T? Resolve(T? unsung) => _isSet ? Value : unsung;
}

/// <summary><see cref="Followable{T}"/> 的工厂方法。</summary>
public static class Followable
{
    /// <summary>跟随未唱。</summary>
    /// <typeparam name="T">值的类型。</typeparam>
    /// <returns>跟随。</returns>
    public static Followable<T> Follow<T>() => default;

    /// <summary>显式取值。</summary>
    /// <typeparam name="T">值的类型。</typeparam>
    /// <param name="value">值；对阴影与辉光，<see langword="null"/> 表示显式关闭。</param>
    /// <returns>显式值。</returns>
    public static Followable<T> Set<T>(T? value) => new(true, value);
}
