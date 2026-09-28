namespace VRC_cantalkcn.Services
{

internal sealed class SenseVoiceSegmentGate
{
    private readonly bool _strict;
    private double _backgroundRms = 0.0005;

    public SenseVoiceSegmentGate(string sensitivity)
    {
        _strict = string.Equals(sensitivity, "strict", StringComparison.OrdinalIgnoreCase);
    }

    // Called only for 512-sample windows outside a VAD speech interval.
    public void ObserveBackground(ReadOnlySpan<float> samples)
    {
        var rms = GetRms(samples);
        if (rms <= 0) return;

        // Limit each update so the beginning of an utterance cannot become the noise floor.
        var bounded = Math.Min(rms, _backgroundRms * 2);
        _backgroundRms = _backgroundRms * 0.95 + bounded * 0.05;
    }

    public bool ShouldDecode(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return false;
        var minimum = _strict ? 0.012 : 0.0035;
        var ratio = _strict ? 5 : 4;
        var requiredRms = Math.Max(minimum, Math.Min(0.04, _backgroundRms * ratio));
        return GetRms(samples) >= requiredRms;
    }

    private static double GetRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sumOfSquares = 0;
        foreach (var sample in samples)
            sumOfSquares += (double)sample * sample;
        return Math.Sqrt(sumOfSquares / samples.Length);
    }
}
}
