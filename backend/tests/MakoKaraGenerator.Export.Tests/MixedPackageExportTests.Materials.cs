using System.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class MixedPackageExportTests
{
    private static Project MaterialProject() => Project.Create("材质成片",Composition.Create(320,180,FrameRate.Fps30,outputDurationUs:700_000),
        LyricsDocument.Create([LyricLine.Create("row","歌词",new(0,700_000),TimingStatus.Edited,
            [LyricToken.Create("token","歌词",new(0,700_000),TimingStatus.Edited)])]),
        lyricStyle:LyricStyle.Default with {FontFamilyName="Noto Sans CJK SC",FontSizePx=40,Stroke=null,Shadow=null,Opacity=.7f});
    private static Task<CompiledEffectPackage> CompileMaterial(EffectPackageContents contents) => CompiledEffectPackage.CompileAsync(
        EffectPackageDefinition.Read(contents),new(Path.Combine(AppContext.BaseDirectory,"script-refs","net10.0")),
        new PackageImageValidator(),new PackageShaderValidator(),new PackageImageGraphValidator(),new PackageMaterialValidator());

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真正区域材质通过正式主Render编码MP4与Alpha并匹配预览(bool transparent)
    {
        using TemporaryDirectory temp=new();var package=await CompileMaterial(MixedEffectPackageFixture.MaterialContents(background:transparent));
        Assert.True(package.Succeeded,string.Join('\n',package.Artifact.Diagnostics));
        var project=MaterialProject();var snapshot=ProjectSnapshot.FromProject(project) with {EffectInputsFingerprint=Fingerprint};
        var effect=package.CreateEffect("controller",new("material-export"),new EffectBinding.Project(),1);
        await using var source=await new PureFrameSourceFactory([effect],Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline=new PreparedPackagePipeline(source,snapshot,package.Definition);
        string path=temp.FilePath(transparent ? "material.mov" : "material.mp4");
        var report=await new VideoExporter(pipeline,new FfmpegMediaProbe(),new FfmpegMediaEncoder()).ExportAsync(project,
            new(){OutputPath=path,IncludeAudio=false,Quality=ExportQuality.High,
                Format=transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4});
        Assert.True(report.IsVerified);Assert.Equal(0,report.FailedFrames);Assert.Equal(package.Resources.GetMaterial("ink"),pipeline.LastMaterial);
        if(transparent)
        {
            var warning=Assert.Single(report.Warnings.Where(item=>item.Contains("TransparentBackgroundDependency",StringComparison.Ordinal)));
            Assert.Contains("material-export",warning);Assert.Contains("材质 ink",warning);Assert.Contains("包内基础外观",warning);
        }
        using var preview=pipeline.Render(RenderRequest.At(snapshot,500_000) with {TransparentBackground=transparent});
        string image=temp.FilePath("frame.png");var start=new ProcessStartInfo("ffmpeg"){RedirectStandardError=true};
        foreach(string arg in new[]{"-hide_banner","-loglevel","error","-i",path,"-vf","select=eq(n\\,15)","-frames:v","1","-y",image})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;string error=await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();Assert.True(process.ExitCode==0,error);
        using var decoded=SKBitmap.Decode(image);double total=0;int count=0,visible=0;
        for(int y=0;y<decoded.Height;y++)for(int x=0;x<decoded.Width;x++)
        {
            Assert.True(preview.TryGetPixel(x,y,out var expected));var actual=decoded.GetPixel(x,y);
            if(transparent){Assert.InRange(Math.Abs(expected.Alpha-actual.Alpha),0,3);if(expected.Alpha<24)continue;}
            if(expected.Blue>30)visible++;
            total+=Math.Abs(expected.Red-actual.Red)+Math.Abs(expected.Green-actual.Green)+Math.Abs(expected.Blue-actual.Blue);count+=3;
        }
        Assert.True(visible>100);Assert.InRange(total/count,0,9);
        if(transparent)Assert.Equal(0,decoded.GetPixel(0,0).Alpha);
    }

    [Fact]
    public async Task 材质中途越出区域阻止编码且保护已有成片()
    {
        using TemporaryDirectory temp=new();string path=temp.FilePath("existing.mp4");byte[] original=[2,7,1,8];await File.WriteAllBytesAsync(path,original);
        string source=MixedEffectPackageFixture.MaterialSource.Replace("frame.Parameters.GetNumber(\"amount\")","frame.TimeUs>=200000 ? 1 : .5");
        var package=await CompileMaterial(MixedEffectPackageFixture.MaterialContents(controllerSource:source,shaderSource:
            "uniform float amount; uniform shader original; half4 main(float2 p) { half4 c=original.eval(p);return amount>.9 ? half4(c.rgb,1) : c; }"));
        Assert.True(package.Succeeded,string.Join('\n',package.Artifact.Diagnostics));
        var project=MaterialProject();var snapshot=ProjectSnapshot.FromProject(project) with {EffectInputsFingerprint=Fingerprint};
        await using var frameSource=await new PureFrameSourceFactory([package.CreateEffect("controller",new("broken-material"),new EffectBinding.Project(),1)],
            Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline=new PreparedPackagePipeline(frameSource,snapshot,package.Definition);
        var failure=await Assert.ThrowsAsync<EffectFailureException>(()=>new VideoExporter(pipeline,new FfmpegMediaProbe(),new FfmpegMediaEncoder()).ExportAsync(project,
            new(){OutputPath=path,IncludeAudio=false,Overwrite=true,Format=ExportVideoFormat.OpaqueMp4}));
        Assert.Equal("MaterialOutputOutsideRegion",failure.Code);Assert.Equal(new EffectInstanceId("broken-material"),failure.InstanceId);
        Assert.Equal(200_000,failure.TimeUs);Assert.Equal(original,await File.ReadAllBytesAsync(path));
    }
}
