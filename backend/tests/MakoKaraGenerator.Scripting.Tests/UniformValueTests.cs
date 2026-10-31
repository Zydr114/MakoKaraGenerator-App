namespace MakoKaraGenerator.Scripting.Tests;

public sealed class UniformValueTests
{
    [Fact]
    public void 帧值数组复制原集合且长度一仍是数组()
    {
        var values = new List<UniformValue> { UniformValue.Float2(1, 2) };
        var array = UniformValue.Array(values); values.Clear();
        Assert.Equal(1, array.ArrayLength); Assert.Equal(UniformValueKind.Float2, array.Kind);
        Assert.Equal<double>([1d, 2d], array.Numbers);
        Assert.Null(UniformValue.Float2(1, 2).ArrayLength);
        Assert.Throws<ArgumentException>(() => UniformValue.Array([]));
        Assert.Throws<ArgumentException>(() => UniformValue.Array([UniformValue.Float(1), UniformValue.Int(1)]));
        Assert.Throws<ArgumentException>(() => UniformValue.Array([array]));
    }

    [Fact]
    public void 浮点禁止非有限且颜色与向量不互换()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UniformValue.Float(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => UniformValue.Float4(0, 0, 0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => UniformValue.Matrix3x3(0, 0, 0, 0, double.NegativeInfinity, 0, 0, 0, 0));
        Assert.Equal(UniformValueKind.Float4, UniformValue.Float4(10, -2, 3, 0).Kind);
        Assert.Equal(UniformValueKind.Color, UniformValue.Color(new(.2, .3, .4, .5)).Kind);
        Assert.Equal(int.MinValue, Assert.Single(UniformValue.Int(int.MinValue).Integers));
    }

    [Fact]
    public void uniform值集固定字典且重复名字不能覆盖()
    {
        var source = new Dictionary<string, UniformValue> { ["a"] = UniformValue.Int(1) };
        var values = new UniformValues(source); source["a"] = UniformValue.Int(2);
        Assert.Equal(1, Assert.Single(values.Values["a"].Integers));
        Assert.Throws<ArgumentException>(() => new UniformValues([new("a", UniformValue.Int(1)), new("a", UniformValue.Int(2))]));
    }
}
