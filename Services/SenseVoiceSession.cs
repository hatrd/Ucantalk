using NAudio.Wave;
using SherpaOnnx;
using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace VRC_cantalkcn.Services;

// One worker owns the native recognizer and VAD. Audio callbacks only enqueue PCM.
internal sealed class SenseVoiceSession
{
    private readonly WaveInEvent _capture;
    private readonly Channel<float[]> _audio = Channel.CreateBounded<float[]>(750);
    private readonly Task _worker;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _suppressed;
    private volatile bool _discardOutput;
    private bool _resetVad;
    private readonly object _captureGate = new();
    private readonly Action<string> _onText;

    private readonly string _modelPath;
    private readonly string _tokensPath;
    private readonly string _vadPath;
    private readonly string _sensitivity;

    public SenseVoiceSession(int deviceNumber, string modelPath, string tokensPath, string vadPath, string sensitivity, Action<string> onText)
    {
        _onText = onText;
        _modelPath = modelPath;
        _tokensPath = tokensPath;
        _vadPath = vadPath;
        _sensitivity = sensitivity;
        _capture = new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 80 };
        _capture.DataAvailable += OnAudio;
        _capture.RecordingStopped += (_, e) =>
        {
            _audio.Writer.TryComplete(e.Exception);
            _stopped.TrySetResult();
        };
        _worker = Task.Run(ProcessAsync);
    }

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task StartAsync()
    {
        try { await _ready.Task; _capture.StartRecording(); }
        catch { _capture.Dispose(); _audio.Writer.TryComplete(); await _worker; throw; }
    }

    public void SetSuppressed(bool value)
    {
        lock (_captureGate) { _suppressed = value; _resetVad = true; }
    }

    public void DiscardOutput() => _discardOutput = true;

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        lock (_captureGate)
        {
            if (_resetVad)
            {
                _audio.Writer.TryWrite([]); // ordered boundary prevents joining speech across TTS playback
                _resetVad = false;
            }
            if (_suppressed) return;
            var samples = new float[e.BytesRecorded / 2];
            for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            if (!_audio.Writer.TryWrite(samples))
                RuntimeLogService.Warn("SenseVoice audio queue full; audio frame dropped.");
        }
    }

    public async Task StopAsync()
    {
        _capture.StopRecording();
        await _stopped.Task;
        _capture.Dispose();
        await _worker; // Flush the last utterance before returning, including push-to-talk.
    }

    private async Task ProcessAsync()
    {
        try
        {
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.SenseVoice.Model = _modelPath;
            config.ModelConfig.SenseVoice.Language = "auto";
            config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            config.ModelConfig.Tokens = _tokensPath;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            using var recognizer = new OfflineRecognizer(config);
            var vadConfig = new VadModelConfig();
            vadConfig.SampleRate = 16000;
            vadConfig.SileroVad.Model = _vadPath;
            var strict = string.Equals(_sensitivity, "strict", StringComparison.OrdinalIgnoreCase);
            vadConfig.SileroVad.Threshold = strict ? 0.8f : 0.7f;
            vadConfig.SileroVad.MinSilenceDuration = 0.6f;
            vadConfig.SileroVad.MinSpeechDuration = strict ? 0.45f : 0.35f;
            vadConfig.SileroVad.MaxSpeechDuration = 20;
            using var vad = new VoiceActivityDetector(vadConfig, 60);
            var gate = new SenseVoiceSegmentGate(_sensitivity);
            _ready.TrySetResult();
            // Silero accepts fixed 512-sample windows; retain the remainder between callbacks.
            var pending = new List<float>();
            await foreach (var samples in _audio.Reader.ReadAllAsync())
            {
                if (samples.Length == 0) { vad.Reset(); pending.Clear(); continue; }
                pending.AddRange(samples);
                var consumed = 0;
                while (pending.Count - consumed >= 512)
                {
                    var frame = pending.GetRange(consumed, 512).ToArray();
                    vad.AcceptWaveform(frame);
                    if (!vad.IsSpeechDetected()) gate.ObserveBackground(frame);
                    consumed += 512;
                    DecodeSegments(vad, recognizer, gate);
                }
                pending.RemoveRange(0, consumed);
            }
            if (pending.Count > 0)
            {
                var tail = new float[512];
                pending.CopyTo(tail);
                vad.AcceptWaveform(tail);
                if (!vad.IsSpeechDetected()) gate.ObserveBackground(tail);
            }
            vad.Flush();
            DecodeSegments(vad, recognizer, gate);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            RuntimeLogService.Error("SenseVoice recognition failed.", ex);
            throw;
        }
    }

    private void DecodeSegments(VoiceActivityDetector vad, OfflineRecognizer recognizer, SenseVoiceSegmentGate gate)
    {
        while (!vad.IsEmpty())
        {
            var samples = vad.Front().Samples;
            vad.Pop();
            if (!gate.ShouldDecode(samples)) continue;

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            recognizer.Decode(stream);
            var text = Regex.Replace(stream.Result.Text, @"<\|[^|]*\|>", "").Trim();
            if (text.Length > 0 && !_suppressed && !_discardOutput) _onText(text);
        }
    }
}
