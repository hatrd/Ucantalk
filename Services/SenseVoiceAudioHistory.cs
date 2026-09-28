namespace VRC_cantalkcn.Services
{

// Keeps enough raw PCM to recover a quiet utterance onset that Silero excludes.
internal sealed class SenseVoiceAudioHistory
{
    private const int SampleRate = 16000;
    private const int PreRollSamples = SampleRate * 3 / 2;
    private const int CapacitySamples = SampleRate * 25; // VAD caps a segment at 20 seconds.

    private readonly float[] _buffer = new float[CapacitySamples];
    private long _nextSample;
    private long _lastSegmentEnd;

    public void Append(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _buffer[(int)(_nextSample % CapacitySamples)] = sample;
            _nextSample++;
        }
    }

    public float[] WithPreRoll(int vadStart, float[] vadSamples)
    {
        if (vadSamples.Length == 0) return vadSamples;

        var start = Math.Max(0L, vadStart);
        var end = start + vadSamples.Length;
        var oldest = Math.Max(0, _nextSample - CapacitySamples);
        var from = Math.Max(Math.Max(start - PreRollSamples, _lastSegmentEnd), oldest);
        _lastSegmentEnd = Math.Max(_lastSegmentEnd, end);

        // Keep the VAD segment if the native start offset cannot be aligned to raw PCM.
        if (start < oldest || end > _nextSample || from >= end) return vadSamples;

        var expanded = new float[(int)(end - from)];
        for (var i = 0; i < expanded.Length; i++)
            expanded[i] = _buffer[(int)((from + i) % CapacitySamples)];
        return expanded;
    }

    public void Reset()
    {
        _nextSample = 0;
        _lastSegmentEnd = 0;
    }
}
}
