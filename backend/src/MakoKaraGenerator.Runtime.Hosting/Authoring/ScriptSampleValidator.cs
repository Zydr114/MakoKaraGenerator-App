using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>自有试样建立、正式双输出验证与媒体释放；不读取当前工程或GUI状态。</summary>
public sealed class ScriptSampleValidator(IRenderFrameSessionFactory sessions,
    Func<string, CancellationToken, Task<TemplateSampleResources>>? createMedia = null)
{
    /// <summary>每种输出的固定验证帧数。</summary>
    public const int FramesPerOutput = 360;
    /// <summary>完整双输出的固定总帧数。</summary>
    public const int TotalFrames = FramesPerOutput * 2;

    /// <summary>建立自有媒体和固定试样，逐帧验证后先释放会话再移除媒体。</summary>
    public async Task<RuntimeValidationResult> ValidateAsync(ScriptSampleRequest request,
        IProgress<RuntimeValidationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var media = new TemplateSampleLease(Path.Combine(Path.GetTempPath(), "makokara-script-validation"));
        try
        {
            var resources = await media.LoadAsync(createMedia ?? TemplateSampleMedia.CreateAsync, cancellationToken).ConfigureAwait(false);
            var snapshot = ScriptSampleBuilder.Build(request, resources);
            return await new RuntimeFrameValidator(sessions).ValidateAsync(snapshot,
                new(FramesPerOutput), progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            string code = error switch
            {
                TemplateSampleException => nameof(ArgumentException),
                PreparedFrameException prepared => prepared.Code, EffectPackageException package => package.Code,
                EffectOutputException output => output.Code, _ => error.GetType().Name,
            };
            return new(false, 0, 0, [new(false, 0, code, error.Message, true)
                { SampleFailureCode = (error as TemplateSampleException)?.Code }]);
        }
    }
}
