using System.Buffers.Binary;
using System.Text;

namespace MakoKaraGenerator.Media.Tests;

public sealed class SelfContainedMovInputValidatorTests
{
    [Theory]
    [InlineData("url ")]
    [InlineData("urn ")]
    [InlineData("alis")]
    public void 标准自包含声明包括QuickTime别名且mdat内容不作为结构扫描(string type)
    {
        byte[] references = References(Entry(type, 1));
        byte[] source = [.. Box("moov", Box("trak", Box("mdia", Box("minf", Box("dinf", references))))),
            .. Box("mdat", "rmra rdrf alis url "u8.ToArray())];
        using MemoryStream stream = new(source); stream.Position = 3;
        SelfContainedMovInputValidator.Validate(stream); Assert.Equal(3L, stream.Position);
    }

    [Theory]
    [InlineData("url ", 0, false)]
    [InlineData("urn ", 0, false)]
    [InlineData("alis", 0, false)]
    [InlineData("url ", 1, true)]
    [InlineData("alis", 1, true)]
    [InlineData("url ", 3, false)]
    [InlineData("xxxx", 1, false)]
    public void 外部声明未知类型保留flags或自包含旗带路径仍明确拒绝(string type, uint flags, bool payload)
    {
        using MemoryStream stream = new(Box("moov", References(Entry(type, flags,
            payload ? "owned-relative-file\0"u8.ToArray() : []))));
        Assert.Throws<IOException>(() => SelfContainedMovInputValidator.Validate(stream));
    }

    [Theory]
    [InlineData("rmra")]
    [InlineData("rdrf")]
    public void 引用影片容器不允许嵌入标准结构层(string type)
    {
        using MemoryStream stream = new(Box("moov", Box("udta", Box(type, []))));
        Assert.Throws<IOException>(() => SelfContainedMovInputValidator.Validate(stream));
    }

    [Fact]
    public void 扩展64位header及标准meta结构保留而越父级边界拒绝()
    {
        using MemoryStream valid = new(Box("moov", Box("meta", [0, 0, 0, 0, .. References(Entry("url ", 1, extended: true))]), extended: true));
        SelfContainedMovInputValidator.Validate(valid);
        byte[] invalid = Box("moov", Box("free", [])); BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(8), 100);
        using MemoryStream malformed = new(invalid);
        Assert.Throws<IOException>(() => SelfContainedMovInputValidator.Validate(malformed));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("boxes")]
    [InlineData("entries")]
    public void 结构检查实际预算拒绝且不把音频时长样本数作为门禁(string budget)
    {
        byte[] bytes;
        if (budget == "depth")
        {
            bytes = Box("free", []);
            for (int depth = 0; depth <= SelfContainedMovInputValidator.MaximumDepth; depth++) bytes = Box("moov", bytes);
        }
        else if (budget == "boxes")
        {
            using MemoryStream source = new();
            for (int index = 0; index <= SelfContainedMovInputValidator.MaximumBoxes; index++) source.Write(Box("free", []));
            bytes = source.ToArray();
        }
        else
        {
            byte[] table = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4), SelfContainedMovInputValidator.MaximumReferenceEntries + 1);
            bytes = Box("dref", table);
        }
        using MemoryStream stream = new(bytes);
        Assert.Throws<IOException>(() => SelfContainedMovInputValidator.Validate(stream));
    }

    [Fact]
    public void 条目计数及尾部必须完整且取消保持原异常()
    {
        byte[] references = References(Entry("url ", 1)); references[^1] = 0;
        using MemoryStream stream = new(references);
        Assert.Throws<IOException>(() => SelfContainedMovInputValidator.Validate(stream));
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        using MemoryStream valid = new(Box("mdat", []));
        Assert.Throws<OperationCanceledException>(() => SelfContainedMovInputValidator.Validate(valid, cancelled.Token));
    }

    internal static byte[] Box(string type, byte[] data, bool extended = false)
    {
        int header = extended ? 16 : 8; byte[] box = new byte[header + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, extended ? 1U : (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        if (extended) BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        data.CopyTo(box, header); return box;
    }

    internal static byte[] Entry(string type, uint flags, byte[]? payload = null, bool extended = false)
    {
        byte[] body = new byte[4 + (payload?.Length ?? 0)]; BinaryPrimitives.WriteUInt32BigEndian(body, flags);
        payload?.CopyTo(body, 4); return Box(type, body, extended);
    }

    internal static byte[] References(byte[] entry) => Box("dref", [0, 0, 0, 0, 0, 0, 0, 1, .. entry]);
}
