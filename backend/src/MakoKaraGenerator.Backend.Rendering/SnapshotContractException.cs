namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>专用快照合同拒绝；不回退到另一输入或工程保存格式。</summary>
public sealed class SnapshotContractException : Exception
{
    /// <summary>创建带稳定编号的合同拒绝。</summary>
    public SnapshotContractException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>稳定的拒绝编号。</summary>
    public string Code { get; }
}
