namespace MakoKaraGenerator.Scripting;

/// <summary>固定时间拖尾采样，不是 UI 帧记录。</summary>
public readonly record struct ParticleTrailSample(long TimeUs, Point2 Position);
