using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering;

/// <summary>后端实际解码固定包图片的门禁；不向脚本暴露 native 对象。</summary>
public interface IPackageImageValidator
{
    /// <summary>检查全部编码像素及后端容量；失败不能把文件作为 SDK 图片公开。</summary>
    void Validate(EffectPackageFile file, CancellationToken cancellationToken = default);
}
