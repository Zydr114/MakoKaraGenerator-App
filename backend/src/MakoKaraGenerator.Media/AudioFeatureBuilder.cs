using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Media;

internal sealed class AudioFeatureBuilder : IDisposable
{
    private readonly Biquad _highPass = new(20, highPass: true);
    private readonly Biquad _lowPass = new(200, highPass: false);
    private readonly IncrementalHash _pcmHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<(double Bass, double Energy, double Onset)> _hops = [];
    private readonly byte[] _hashBuffer = new byte[4096 * sizeof(float)];
    private static readonly double Smoothing = 1 - Math.Exp(-0.02 / 0.1);
    private double _bassEnergy, _fullEnergy, _bassEnvelope, _energyBaseline;
    private int _fill;
    public long SampleCount { get; private set; }
    public void Append(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < samples.Length; offset += 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = samples.Slice(offset, Math.Min(4096, samples.Length - offset));
            for (int i = 0; i < block.Length; i++)
            {
                float value = block[i];
                if (!float.IsFinite(value)) throw new AudioAnalysisException("NonFiniteAudioSample", "音频 PCM 含非有限采样，未将其替换为静音。");
                BinaryPrimitives.WriteSingleLittleEndian(_hashBuffer.AsSpan(i * sizeof(float)), value);
                double filtered = _lowPass.Process(_highPass.Process(value));
                _bassEnergy += filtered * filtered; _fullEnergy += (double)value * value;
                _fill++; SampleCount = checked(SampleCount + 1);
                if (_fill == AudioFeatureAnalyzer.HopSamples) Flush();
            }
            _pcmHash.AppendData(_hashBuffer.AsSpan(0, block.Length * sizeof(float)));
        }
    }
    private void Flush()
    {
        double bass = Math.Sqrt(_bassEnergy / _fill), energy = Math.Sqrt(_fullEnergy / _fill);
        // 最后一个短 hop 使用其真实长度，不凭空补零。
        double alpha = _fill == AudioFeatureAnalyzer.HopSamples ? Smoothing : 1 - Math.Exp(-_fill / (double)AudioFeatureAnalyzer.SampleRate / .1);
        _bassEnvelope += alpha * (bass - _bassEnvelope);
        double onset = Math.Max(0, energy - _energyBaseline);
        _energyBaseline += alpha * (energy - _energyBaseline);
        _hops.Add((_bassEnvelope, energy, onset)); _fill = 0; _bassEnergy = 0; _fullEnergy = 0;
    }
    public AudioFeatureAnalysis Complete(string sourceHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SampleCount == 0) throw new AudioAnalysisException("AudioEmptyDecodedStream", "音轨没有实际解码采样。");
        if (_fill != 0) Flush();
        string pcmHash = Convert.ToHexStringLower(_pcmHash.GetHashAndReset());
        string revision = AudioAnalysisProfile.Revision(sourceHash, pcmHash, SampleCount);
        long duration = AudioAnalysisProfile.DurationUs(SampleCount);
        double maxBass = _hops.Max(static hop => hop.Bass), maxOnset = _hops.Max(static hop => hop.Onset), maxEnergy = _hops.Max(static hop => hop.Energy);
        // 与整曲真实音量比较，不能把纯高音的微小滤波残余归一成满低频。
        double bassReference = Math.Max(maxBass, maxEnergy);
        List<int> peaks = [];
        for (int i = 0; i < _hops.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); double onset = _hops[i].Onset;
            if (onset <= 0 || onset < Math.Max(.25 * maxOnset, .15 * maxEnergy)
                || i > 0 && onset <= _hops[i - 1].Onset || i + 1 < _hops.Count && onset < _hops[i + 1].Onset) continue;
            if (peaks.Count == 0 || i - peaks[^1] >= 8) peaks.Add(i);
        }
        var points = ImmutableArray.CreateBuilder<AudioFeaturePoint>(_hops.Count);
        Queue<long> intervals = new(); int peakIndex = 0; long? lastPeak = null;
        for (int i = 0; i < _hops.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); long time = (long)i * 20_000;
            if (peakIndex < peaks.Count && peaks[peakIndex] == i)
            {
                if (lastPeak is { } previous)
                {
                    long interval = time - previous;
                    if (interval is >= 250_000 and <= 2_000_000)
                    { intervals.Enqueue(interval); if (intervals.Count > 8) intervals.Dequeue(); }
                    else intervals.Clear();
                }
                lastPeak = time; peakIndex++;
            }
            double? phase = null; double confidence = 0;
            if (intervals.Count >= 3 && lastPeak is { } last)
            {
                var ordered = intervals.Order().ToArray();
                double period = ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2d : ordered[ordered.Length / 2];
                double deviation = intervals.Max(value => Math.Abs(value - period) / period);
                if (deviation <= .2 && time - last < 2 * period)
                { phase = (time - last) % period / period; confidence = 1 - intervals.Average(value => Math.Abs(value - period) / period) / .2; }
            }
            var hop = _hops[i];
            points.Add(new(time, new(bassReference == 0 ? 0 : hop.Bass / bassReference, maxOnset == 0 ? 0 : hop.Onset / maxOnset, phase, confidence)));
        }
        return new(new AudioFeatures(revision, duration, points.MoveToImmutable()), sourceHash, pcmHash, SampleCount);
    }
    public void Dispose() => _pcmHash.Dispose();
    // https://www.w3.org/TR/audio-eq-cookbook/ LPF/HPF, a0 归一化，Q=1/sqrt(2)。
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;
        public Biquad(double frequency, bool highPass)
        {
            double angle = 2 * Math.PI * frequency / AudioFeatureAnalyzer.SampleRate;
            double cosine = Math.Cos(angle), alpha = Math.Sin(angle) / Math.Sqrt(2), a0 = 1 + alpha;
            _b0 = (highPass ? 1 + cosine : 1 - cosine) / 2 / a0; _b2 = _b0;
            _b1 = (highPass ? -(1 + cosine) : 1 - cosine) / a0;
            _a1 = -2 * cosine / a0; _a2 = (1 - alpha) / a0;
        }
        public double Process(double input)
        {
            double output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = input; _y2 = _y1; _y1 = output; return output;
        }
    }
}
