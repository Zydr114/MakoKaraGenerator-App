using System.Reflection;
using System.Runtime.Loader;

namespace MakoKaraGenerator.Scripting.Loading;

/// <summary>可回收的固定 DLL 代次；宿主 SDK 始终与默认加载上下文共用。</summary>
public sealed class ScriptModule : IDisposable
{
    private ScriptLoadContext? _context;
    private Assembly? _assembly;
    private readonly List<IDisposable> _instances = [];

    /// <summary>从固定 DLL／PDB 字节建立独立代次。</summary>
    /// <param name="peImage">可重建的编译 DLL 字节。</param>
    /// <param name="pdbImage">portable PDB，可为空。</param>
    public ScriptModule(ReadOnlyMemory<byte> peImage, ReadOnlyMemory<byte> pdbImage = default)
    {
        _context = new ScriptLoadContext();
        UnloadReference = new WeakReference(_context);
        try
        {
            using MemoryStream dll = new(peImage.ToArray(), writable: false);
            using MemoryStream pdb = new(pdbImage.ToArray(), writable: false);
            _assembly = _context.LoadFromStream(dll, pdbImage.IsEmpty ? null : pdb);
        }
        catch
        {
            _context.Unload();
            _context = null;
            throw;
        }
    }

    /// <summary>正常释放后可用于验证加载代次回收的弱引用；不会维持其生命周期。</summary>
    public WeakReference UnloadReference { get; }

    /// <summary>创建具有公共无参构造的入口；该代次拥有返回实例的释放职责。</summary>
    /// <typeparam name="T">宿主期望的接口。</typeparam>
    /// <param name="entryType">源码定义的完整类型名。</param>
    /// <returns>同一 SDK 类型系统中的入口。</returns>
    public T CreateInstance<T>(string entryType) where T : class
    {
        Type type = Entry<T>(entryType);
        T instance = (T)Activator.CreateInstance(type)!;
        if (instance is IDisposable disposable) _instances.Add(disposable);
        return instance;
    }

    /// <summary>只检查真实入口类型及公共无参构造，不运行作者构造、初始化或释放。</summary>
    public void ValidateEntry<T>(string entryType) where T : class => _ = Entry<T>(entryType);

    private Type Entry<T>(string entryType) where T : class
    {
        ObjectDisposedException.ThrowIf(_context is null, this); ArgumentException.ThrowIfNullOrWhiteSpace(entryType);
        Type type = _assembly!.GetType(entryType, throwOnError: true)!;
        if (!typeof(T).IsAssignableFrom(type) || !type.IsClass || !type.IsVisible || type.IsAbstract || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException("脚本入口必须为公共非抽象类型，实现指定接口并具有公共无参构造。" + entryType);
        return type;
    }

    /// <summary>释放入口并请求合作卸载；即使用户 Dispose 出错也不保留宿主强引用。</summary>
    public void Dispose()
    {
        if (_context is not { } context) return;
        _context = null;
        List<Exception> failures = [];
        foreach (IDisposable instance in _instances)
        {
            try { instance.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        _instances.Clear();
        _assembly = null;
        context.Unload();
        if (failures.Count > 0) throw new AggregateException("脚本实例释放失败。", failures);
        GC.SuppressFinalize(this);
    }

    private sealed class ScriptLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly sdk = typeof(TokenId).Assembly;
            if (AssemblyName.ReferenceMatchesDefinition(sdk.GetName(), assemblyName)) return sdk;
            // v1 仅允许分发 BCL 与 SDK；不自动解析外部 NuGet 或 native 插件。
            return null;
        }
    }
}
