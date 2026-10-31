namespace MakoKaraGenerator.Scripting.Tests;

public sealed class BurstWriterContractTests
{
    private static TokenStarted Event => new(new(new("token"), new("branch"), new("line"), "字", new(100,200), SourceTimingStatus.Edited, 0), new(0,0,0), ReplayReason.Initial);
    private static BurstDescriptor Description => new() { Count = 2, Anchor = new BurstAnchor.SourceToken(new("token")),
        LifetimeUs = 1, SizePx = 2, Color = new(1,0,0,1), SeedStreamKey = "event" };
    [Fact]
    public void 当前事件同发射器最后登记且退出后不能再次使用()
    {
        var evt = Event; BurstWriter writer = new(new("instance"), EffectCapabilities.Overlay, evt);
        writer.Queue(new("burst"), Description); writer.Queue(new("burst"), Description with { Count = 3 });
        var intent = Assert.Single(writer.Complete()); Assert.Equal(3, intent.Descriptor.Count);
        Assert.Equal(evt.EventId, intent.EventId); Assert.Equal(100, intent.TimeUs);
        Assert.Equal("InvalidBurstOutputStage", Assert.Throws<EffectOutputException>(() => writer.Queue(new("burst"), Description)).Code);
        Assert.Equal("InvalidBurstOutputStage", Assert.Throws<EffectOutputException>(() => writer.Complete()).Code);
    }
    [Fact]
    public void 无能力及非法身份数量或物理参数不发布意图()
    {
        BurstWriter denied = new(new("instance"), EffectCapabilities.None, Event);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => denied.Queue(new("burst"), Description)).Code);
        BurstWriter writer = new(new("instance"), EffectCapabilities.Overlay, Event);
        Assert.Equal("InvalidBurst", Assert.Throws<EffectOutputException>(() => writer.Queue(default, Description)).Code);
        foreach (var desc in new[] { Description with { Count = -1 }, Description with { Anchor = new BurstAnchor.SourceToken(default) },
            Description with { Anchor = new BurstAnchor.BindingCursors((CursorAnchorMode)100) } })
            Assert.Equal("InvalidBurst", Assert.Throws<EffectOutputException>(() => writer.Queue(new("burst"), desc)).Code);
        Assert.Equal("InvalidEmitter", Assert.Throws<EffectOutputException>(() => writer.Queue(new("burst"), Description with { LifetimeUs = 0 })).Code);
        Assert.Empty(writer.Complete());
        Assert.Equal("InvalidBurstOutputStage", Assert.Throws<EffectOutputException>(() => BurstWriter.Unavailable.Queue(new("burst"), Description)).Code);
    }
    [Fact]
    public void 非光标来源只保留真实装载而不制造轨道()
    {
        var token = new ParticleSource.Token(new(new("token"), new("binding")));
        var line = new ParticleSource.Line(new(new("line"), new("binding")));
        var mark = new ParticleSource.TextMark(new(new("mark"), new("cue"), new("binding")));
        foreach (var source in new ParticleSource[] { token, line, mark })
        { Assert.Null(source.TrackId); Assert.Equal(new BindingId("binding"), source.BindingId); }
        Assert.Throws<ArgumentException>(() => new ParticleSource.Token(new(default, new("binding"))));
        Assert.Throws<ArgumentException>(() => new ParticleSource.TextMark(new(new("mark"), default, new("binding"))));
        Assert.Throws<ArgumentException>(() => new ParticleSource.Cursor(default, new(new("branch"), new("line"), new("binding"),
            new(0,100), ProgressSource.Line)));
        var birth = new ParticleBirthSample(new(new("instance"), new("burst"),0), token,100,101,new(1,2),Description,Event.EventId);
        var schedule = new EmissionSchedule(100,200,[birth]); Assert.Null(Assert.Single(schedule.Births).TrackId);
        Assert.Throws<ArgumentException>(() => new EmissionSchedule(0,100,[birth]));
        Assert.Throws<ArgumentException>(() => new EmissionSchedule(100,200,[birth with { Source = null! }]));
    }
}
