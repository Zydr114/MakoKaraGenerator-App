namespace MakoKaraGenerator.Scripting;

/// <summary>粒子出生的真实来源；固定文字不伪造播放头轨道。</summary>
public abstract record ParticleSource
{
    private ParticleSource() { }
    /// <summary>来源的真实装载。</summary>
    public abstract BindingId BindingId { get; }
    /// <summary>真实光标及其桥接来源的轨道身份。</summary>
    public CursorTrackId? TrackId => this switch { Cursor cursor => cursor.Track, Bridge bridge => bridge.Track, _ => null };
    /// <summary>真实播放头及其出生时的源身份。</summary>
    public sealed record Cursor : ParticleSource
    {
        /// <summary>建立真实轨道与源身份。</summary>
        public Cursor(CursorTrackId trackId, CursorIdentity identity)
        { if (!trackId.IsValid) throw new ArgumentException("轨道身份无效。", nameof(trackId)); ArgumentNullException.ThrowIfNull(identity); Track = trackId; Identity = identity; }
        /// <summary>真实轨道。</summary>
        public CursorTrackId Track { get; }
        /// <summary>出生时的源与装载。</summary>
        public CursorIdentity Identity { get; }
        /// <inheritdoc/>
        public override BindingId BindingId => Identity.BindingId;
    }
    /// <summary>真实后继间的飞行来源；不是演唱区间内的 Current。</summary>
    public sealed record Bridge : ParticleSource
    {
        /// <summary>固定真实后继与空档末段的正半开飞行区间。</summary>
        public Bridge(CursorTransitionEvent transition, TimeRange flightRange)
        {
            ArgumentNullException.ThrowIfNull(transition);
            if (!flightRange.IsValid || transition.GapUs <= 0 || transition.TimeUs != transition.To.TimeRange.StartUs
                || flightRange.StartUs < transition.From.TimeRange.EndUs || flightRange.EndUs != transition.To.TimeRange.StartUs)
                throw new ArgumentException("桥接须使用真实后继的正空档末段。", nameof(flightRange));
            Track = transition.TrackId; From = transition.From; To = transition.To; TransitionId = transition.EventId; FlightRange = flightRange;
        }
        /// <summary>真实分支／程序轨道。</summary>
        public CursorTrackId Track { get; }
        /// <summary>旧端实际源／装载，仍保留其原演唱区间。</summary>
        public CursorIdentity From { get; }
        /// <summary>新端实际源／装载，不冒充空档内演唱。</summary>
        public CursorIdentity To { get; }
        /// <summary>固定转换身份，不依赖重放原因。</summary>
        public EventId TransitionId { get; }
        /// <summary>本次实际半开飞行区间。</summary>
        public TimeRange FlightRange { get; }
        /// <inheritdoc/>
        public override BindingId BindingId => From.BindingId;
    }
    /// <summary>真实基础元素族装载。</summary>
    public sealed record Token : ParticleSource
    {
        /// <summary>建立元素来源。</summary>
        public Token(TokenFamilyTarget target)
        { ArgumentNullException.ThrowIfNull(target); if (!target.TokenId.IsValid || !target.BindingId.IsValid) throw new ArgumentException("元素装载无效。", nameof(target)); Target = target; }
        /// <summary>真实元素与装载。</summary>
        public TokenFamilyTarget Target { get; }
        /// <inheritdoc/>
        public override BindingId BindingId => Target.BindingId;
    }
    /// <summary>真实歌词行装载。</summary>
    public sealed record Line : ParticleSource
    {
        /// <summary>建立行来源。</summary>
        public Line(LineTarget target)
        { ArgumentNullException.ThrowIfNull(target); if (!target.LineId.IsValid || !target.BindingId.IsValid) throw new ArgumentException("行装载无效。", nameof(target)); Target = target; }
        /// <summary>真实行与装载。</summary>
        public LineTarget Target { get; }
        /// <inheritdoc/>
        public override BindingId BindingId => Target.BindingId;
    }
    /// <summary>真实文字标记装载。</summary>
    public sealed record TextMark : ParticleSource
    {
        /// <summary>建立标记来源。</summary>
        public TextMark(TextMarkTarget target)
        { ArgumentNullException.ThrowIfNull(target); if (!target.NodeId.IsValid || !target.CueId.IsValid || !target.BindingId.IsValid) throw new ArgumentException("标记装载无效。", nameof(target)); Target = target; }
        /// <summary>真实标记、前奏与装载。</summary>
        public TextMarkTarget Target { get; }
        /// <inheritdoc/>
        public override BindingId BindingId => Target.BindingId;
    }
}
