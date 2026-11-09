using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Backend.Worker;

// A hard GPU stream failure reports a stable code plus code-identity details: the root exception type,
// its throwing method and engine-generated graph/budget text. Author or script messages are never copied.
internal sealed record GpuStreamFailure(string Code, string Type, string? Site, string? Detail)
{
    internal const string FallbackCode = "WorkerGpuStreamFailed";
    private const int MaximumText = 512;

    internal static GpuStreamFailure From(Exception failure)
    {
        Exception? coded = failure;
        while (coded is not null && CodeOf(coded) is null) coded = coded.InnerException;
        Exception root = failure.GetBaseException();
        var site = root.TargetSite ?? failure.TargetSite;
        return new(coded is null ? FallbackCode : CodeOf(coded)!, root.GetType().Name,
            site is null ? null : Bound(site.DeclaringType?.Name + "." + site.Name),
            coded is GpuPreviewException or ImageGraphException or ImageSurfaceBudgetException ? Bound(coded.Message) : null);
    }

    private static string? CodeOf(Exception failure) => failure switch
    {
        WorkerProtocolException protocol => protocol.Code,
        GpuPreviewException gpu => gpu.Code,
        EffectFailureException effect => effect.Code,
        RenderSessionFailureException session => session.Code,
        ImageGraphException graph => graph.Code,
        ImageSurfaceBudgetException budget => budget.Code,
        ShaderProgramException shader => shader.Code,
        EffectPackageException package => package.Code,
        _ => null,
    };

    private static string Bound(string value)
    {
        var text = new StringBuilder(Math.Min(value.Length, MaximumText));
        foreach (char ch in value)
        {
            if (text.Length == MaximumText) break;
            text.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return text.ToString();
    }
}
