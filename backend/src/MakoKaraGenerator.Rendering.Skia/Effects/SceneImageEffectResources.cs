using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>主画布借用的帧外固定程序及图片；不编译作者源码，不拥有传入程序／图片。</summary>
/// <remarks>调用方须在管线和所有帧释放后释放本集合，再释放借用的包资源。</remarks>
public sealed class SceneImageEffectResources : IDisposable
{
    private readonly ImmutableDictionary<GraphHandle, SceneImageGraphProgram> _programs;
    private readonly ImmutableDictionary<GraphHandle, ImmutableDictionary<string, SkiaGraphInput>> _images;
    private readonly ImmutableDictionary<MaterialHandle, SceneMaterialProgram> _materials;
    private readonly ImmutableDictionary<MaterialHandle, ImmutableDictionary<string, SkiaGraphInput>> _materialImages;
    private readonly ImmutableDictionary<ImageHandle, SkiaGraphSurface> _spriteImages;
    private bool _disposed;
    internal bool HasSpriteImages => !_spriteImages.IsEmpty;

    /// <summary>固定明确的程序捕获框和真实图片输入；重复程序、非资源图片或未声明输入拒绝。</summary>
    public SceneImageEffectResources(IEnumerable<SceneImageGraphProgram> programs,
        IEnumerable<KeyValuePair<GraphHandle, IReadOnlyDictionary<string, SkiaGraphInput>>>? images = null)
        : this(programs, images, []) { }
    /// <summary>固定独立材质程序及其真实图片，不默认拉伸资源到画布。</summary>
    public SceneImageEffectResources(IEnumerable<SceneImageGraphProgram> programs,
        IEnumerable<KeyValuePair<GraphHandle, IReadOnlyDictionary<string, SkiaGraphInput>>>? images,
        IEnumerable<SceneMaterialProgram> materials,
        IEnumerable<KeyValuePair<MaterialHandle, IReadOnlyDictionary<string, SkiaGraphInput>>>? materialImages = null,
        IEnumerable<KeyValuePair<ImageHandle, SkiaGraphSurface>>? spriteImages = null)
    {
        ArgumentNullException.ThrowIfNull(programs);
        _programs = programs.ToImmutableDictionary(program => program.Handle);
        _materials = materials.ToImmutableDictionary(program => program.Handle);
        var builder = ImmutableDictionary.CreateBuilder<GraphHandle, ImmutableDictionary<string, SkiaGraphInput>>();
        foreach (var pair in images ?? [])
        {
            if (!_programs.TryGetValue(pair.Key, out var program)) throw new ArgumentException("图片输入须属于固定程序。");
            foreach (var input in pair.Value)
            {
                var declaration = program.Graph.Plan.Definition.Inputs.SingleOrDefault(item => item.Id == input.Key);
                if (declaration is null || input.Value.Source != GraphInputSource.ResourceImage || declaration.ResourceId != input.Value.ResourceId
                    || declaration.Source != GraphInputSource.ResourceImage && !declaration.Choices.ContainsValue(GraphInputSource.ResourceImage))
                    throw new ArgumentException("只接受具备真实资源身份的已声明包图片输入。");
            }
            builder.Add(pair.Key, pair.Value.ToImmutableDictionary(StringComparer.Ordinal));
        }
        _images = builder.ToImmutable();
        var materialBuilder = ImmutableDictionary.CreateBuilder<MaterialHandle, ImmutableDictionary<string, SkiaGraphInput>>();
        foreach (var pair in materialImages ?? [])
        {
            if (!_materials.TryGetValue(pair.Key, out var program)) throw new ArgumentException("图片须属于真实固定材质。");
            foreach (var input in pair.Value)
            {
                var declaration = program.Graph.Plan.Definition.Inputs.SingleOrDefault(item => item.Id == input.Key);
                if (declaration is null || input.Value.Source != GraphInputSource.ResourceImage || declaration.ResourceId != input.Value.ResourceId
                    || declaration.Source != GraphInputSource.ResourceImage && !declaration.Choices.ContainsValue(GraphInputSource.ResourceImage))
                    throw new ArgumentException("材质图片输入须有真实已声明资源身份。");
            }
            materialBuilder.Add(pair.Key, pair.Value.ToImmutableDictionary(StringComparer.Ordinal));
        }
        _materialImages = materialBuilder.ToImmutable();
        _spriteImages = (spriteImages ?? []).ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var surface in _spriteImages.Values)
            if (surface.Kind != ShaderSurfaceKind.Color || surface.Descriptor.LocalToCanvas != Core.Projects.SceneMatrix.Identity
                || surface.Descriptor.DensityX != 1 || surface.Descriptor.DensityY != 1
                || surface.Descriptor.Bounds != new GraphRect(0, 0, surface.Image.Width, surface.Image.Height))
                throw new ArgumentException("精灵资源须借用完整原始彩色图片，不能用图产物或目标框替代。");
    }
    internal SkiaGraphSurface GetSpriteImage(ImageHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _spriteImages.TryGetValue(handle, out var image) ? image
            : throw new PreparedFrameException("SpriteResourceUnavailable", "没有精灵的真实固定包修订图片，不能忽略输出。");
    }
    internal IReadOnlyDictionary<string, SkiaGraphInput> GetMaterialImages(MaterialHandle handle)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _materialImages.GetValueOrDefault(handle) ?? ImmutableDictionary<string, SkiaGraphInput>.Empty; }
    internal SceneMaterialProgram GetMaterial(MaterialHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _materials.TryGetValue(handle, out var program) ? program
            : throw new PreparedFrameException("MaterialResourceUnavailable", "没有真实固定修订材质程序，不能忽略材质。");
    }
    internal SceneImageGraphProgram GetProgram(GraphHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _programs.TryGetValue(handle, out var program) ? program
            : throw new PreparedFrameException("ImageEffectResourceUnavailable", "没有当前固定修订的图程序，不能忽略效果。");
    }
    internal IReadOnlyDictionary<string, SkiaGraphInput> GetImages(GraphHandle handle)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _images.GetValueOrDefault(handle) ?? ImmutableDictionary<string, SkiaGraphInput>.Empty; }
    /// <summary>结束本集合的借用入口，保留调用方拥有的包图／图片；幂等。</summary>
    public void Dispose() { _disposed = true; }
}
