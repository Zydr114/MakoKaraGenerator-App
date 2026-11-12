namespace MakoKaraGenerator.Scripting.Tests;

public sealed class MaterialUniformWriterTests
{
    private static readonly TokenFamilyTarget Target = new(new("token"), new("binding"));
    private static readonly ResourceCatalog Resources = new("package", "sha256:" + new string('a', 64), ["ink", "other"], [], []);
    private static readonly MaterialHandle Handle = Resources.GetMaterial("ink");
    private static readonly MaterialValue White = MaterialValue.Solid(new(1, 1, 1, 1));
    private static readonly StyleView Basis = TestStyles.View(White, White, new(2, White), null, .5);
    private static readonly MaterialValue Package = MaterialValue.PackageExport(Handle, ParameterValues.Empty);
    private static FrameWriter Writer(EffectCapabilities capability = EffectCapabilities.Style, bool independent = true)
        => new([new() { BranchId = new("branch"), LineId = new("line"), WithRuby = Target, BaseOnly = new(Target.TokenId, Target.BindingId),
            BaseGeometry = new() { AdvanceBounds = new(0, 0, 20, 20), Baseline = new(0, 15), BaseWorldMatrix = Affine2.Identity,
                CanTransformIndependently = independent },
            FamilyGeometry = new() { AdvanceBounds = new(0, 0, 20, 20), Baseline = new(0, 15), BaseWorldMatrix = Affine2.Identity } }],
            capability, new([new(Target.TokenId, Basis)]), Resources);
    private static void Set(FrameWriter writer, MaterialSlot slot)
    {
        if (slot == MaterialSlot.UnsungFill) writer.Style.SetUnsungMaterial(Target, Package);
        else if (slot == MaterialSlot.SungFill) writer.Style.SetSungMaterial(Target, Package);
        else writer.Style.SetStroke(Target, new(4, Package));
    }
    private static StyleView Resolve(FrameWriter writer) => Assert.Single(writer.Complete().TokenStyles).Update.Apply(Basis);

    [Theory]
    [InlineData(MaterialSlot.UnsungFill)] [InlineData(MaterialSlot.SungFill)] [InlineData(MaterialSlot.UnsungStroke)] [InlineData(MaterialSlot.SungStroke)]
    public void 显式部位只修改自身且每个节点同名值分别保存(MaterialSlot slot)
    {
        var writer = Writer(); Set(writer, slot);
        writer.Materials.SetUniform(Handle, Target, slot, "a", "amount", UniformValue.Float(.25));
        writer.Materials.SetUniform(Handle, Target, slot, "b", "amount", UniformValue.Float(.5));
        writer.Materials.SetUniform(Handle, Target, slot, "a", "amount", UniformValue.Float(.75));
        var style = Resolve(writer);
        var material = Assert.IsType<MaterialValue.PackageMaterial>(style.Material(slot));
        Assert.Equal(.75, Assert.Single(material.Uniforms.Values[("a", "amount")].Numbers));
        Assert.Equal(.5, Assert.Single(material.Uniforms.Values[("b", "amount")].Numbers));
        Assert.Equal(2, material.Uniforms.Values.Count); Assert.Empty(Assert.IsType<MaterialValue.PackageMaterial>(Package).Uniforms.Values);
        Assert.Equal(.5, style.Opacity); Assert.Equal(slot is MaterialSlot.UnsungStroke or MaterialSlot.SungStroke ? 4 : 2, style.Unsung.InnermostStroke!.WidthPx);
        if (slot != MaterialSlot.UnsungFill) Assert.Same(White, style.Unsung.Fill);
        if (slot != MaterialSlot.SungFill) Assert.Same(White, style.Sung.Fill);
    }

    [Fact]
    public void 再次Set清除旧覆盖而后续uniform再次建立新覆盖()
    {
        var writer = Writer(); Set(writer, MaterialSlot.SungFill);
        writer.Materials.SetUniform(Handle, Target, MaterialSlot.SungFill, "a", "amount", UniformValue.Float(.2));
        Set(writer, MaterialSlot.SungFill);
        writer.Materials.SetUniform(Handle, Target, MaterialSlot.SungFill, "b", "amount", UniformValue.Float(.7));
        var material = Assert.IsType<MaterialValue.PackageMaterial>(Resolve(writer).Sung.Fill);
        Assert.Equal(("b", "amount"), Assert.Single(material.Uniforms.Values).Key);
    }

    [Fact]
    public void 无材质时写uniform即使后来Set也不能掩盖错误()
    {
        var writer = Writer(); writer.Materials.SetUniform(Handle, Target, MaterialSlot.SungFill, "a", "amount", UniformValue.Float(.2));
        Set(writer, MaterialSlot.SungFill);
        Assert.Equal("MaterialUniformIdentityMismatch", Assert.Throws<EffectOutputException>(() => Resolve(writer)).Code);
    }

    [Fact]
    public void 同包另一个合法材质句柄不能冒充当前部位材质()
    {
        var writer = Writer(); Set(writer, MaterialSlot.SungFill);
        writer.Materials.SetUniform(Resources.GetMaterial("other"), Target, MaterialSlot.SungFill, "a", "amount", UniformValue.Float(.5));
        Assert.Equal("MaterialUniformIdentityMismatch", Assert.Throws<EffectOutputException>(() => Resolve(writer)).Code);
    }

    [Fact]
    public void 独立求值的相同操作和值按内容相等而顺序和值变化不相等()
    {
        StyleUpdate Build(double value)
        {
            var writer = Writer(); Set(writer, MaterialSlot.SungFill);
            writer.Materials.SetUniform(Handle, Target, MaterialSlot.SungFill, "a", "amount", UniformValue.Float(value));
            return Assert.Single(writer.Complete().TokenStyles).Update;
        }
        var first = Build(.5); var replay = Build(.5); var changed = Build(.7);
        Assert.Equal(first, replay); Assert.Equal(first.GetHashCode(), replay.GetHashCode()); Assert.NotEqual(first, changed);
        Assert.NotEqual(first, first.Then(new(sung: new(fill: Package))));
    }

    [Fact]
    public void uniform沿调用实例顺序组合且不修改参数真值()
    {
        var first = Writer(); Set(first, MaterialSlot.SungFill);
        var second = Writer(); second.Materials.SetUniform(Handle, Target, MaterialSlot.SungFill, "a", "amount", UniformValue.Float(.9));
        var combined = Assert.Single(first.Complete().TokenStyles).Update.Then(Assert.Single(second.Complete().TokenStyles).Update);
        var a = Assert.IsType<MaterialValue.PackageMaterial>(combined.Apply(Basis).Sung.Fill);
        var b = Assert.IsType<MaterialValue.PackageMaterial>(combined.Apply(Basis).Sung.Fill);
        Assert.Equal(a, b); Assert.Equal(a.GetHashCode(), b.GetHashCode()); Assert.Same(ParameterValues.Empty, a.Parameters);
        Assert.Empty(Assert.IsType<MaterialValue.PackageMaterial>(Package).Uniforms.Values);
    }

    [Fact]
    public void 写入仍须真实目标固定资源写权字簇独立及有效阶段()
    {
        var writer = Writer();
        void Put(FrameWriter target, MaterialHandle handle, TokenFamilyTarget token) => target.Materials.SetUniform(handle, token,
            MaterialSlot.SungFill, "a", "amount", UniformValue.Float(.2));
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Put(Writer(EffectCapabilities.Transforms), Handle, Target)).Code);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => Put(Writer(independent: false), Handle, Target)).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Put(writer, Handle, Target with { BindingId = new("wrong") })).Code);
        var other = new ResourceCatalog("package", "sha256:" + new string('b', 64), ["ink"], [], []);
        Assert.Equal("InvalidMaterialResource", Assert.Throws<EffectOutputException>(() => Put(writer, other.GetMaterial("ink"), Target)).Code);
        Assert.Equal("InvalidMaterialSlot", Assert.Throws<EffectOutputException>(() => writer.Materials.SetUniform(Handle, Target,
            (MaterialSlot)99, "a", "amount", UniformValue.Float(.2))).Code);
        writer.Complete(); Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => Put(writer, Handle, Target)).Code);
    }
}
