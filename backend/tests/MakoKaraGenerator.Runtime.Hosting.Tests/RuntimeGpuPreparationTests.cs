using System.Collections.Immutable;
using System.Reflection;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class RuntimeGpuPreparationTests
{
    [Fact]
    public void Managed_preparation_uses_loaded_implementation_images_without_creating_devices()
    {
        Type device = typeof(SkiaRenderPipeline).Assembly.GetType("MakoKaraGenerator.Rendering.Skia.VulkanSkiaRenderDevice", throwOnError: true)!;
        int Count(string name) => (int)device.GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int devices = Count("DeviceInitializations"), contexts = Count("ContextInitializations");
        MethodInfo prepare = typeof(RuntimeGpuPreparation).GetMethod("PrepareRuntimeDependencies", BindingFlags.Static | BindingFlags.NonPublic)!;
        var identities = Assert.IsType<ImmutableArray<RuntimeLoadedAssemblyIdentity>>(prepare.Invoke(null, null));
        Assert.NotEmpty(identities);
        Assert.Equal(identities.Length, identities.Select(x => x.AssemblyIdentity).Distinct(StringComparer.Ordinal).Count());
        foreach (Assembly required in new[] { typeof(ProjectSnapshot).Assembly, typeof(RenderRequest).Assembly,
                     typeof(SkiaRenderPipeline).Assembly, typeof(ProjectRenderSessionFactory).Assembly })
            Assert.Contains(identities, identity => identity.AssemblyIdentity == required.FullName &&
                identity.ModuleVersionId == required.ManifestModule.ModuleVersionId.ToString("N"));
        foreach (var identity in identities)
        {
            Assembly implementation = Assert.Single(AppDomain.CurrentDomain.GetAssemblies(),
                assembly => assembly.FullName == identity.AssemblyIdentity &&
                    assembly.ManifestModule.ModuleVersionId.ToString("N") == identity.ModuleVersionId);
            Assert.DoesNotContain("/script-refs/", implementation.Location, StringComparison.Ordinal);
        }
        Assert.Equal(devices, Count("DeviceInitializations"));
        Assert.Equal(contexts, Count("ContextInitializations"));
    }
}
