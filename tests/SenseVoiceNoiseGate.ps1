$ErrorActionPreference = 'Stop'
$gate = Get-Content (Join-Path $PSScriptRoot '../Services/SenseVoiceSegmentGate.cs') -Raw
$source = "using System;`n" + $gate + @'

public static class GateChecks
{
    private static float[] Tone(double amplitude, int count)
    {
        var samples = new float[count];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(i * 0.37));
        return samples;
    }

    public static bool RejectsLowNoise()
    {
        var gate = new VRC_cantalkcn.Services.SenseVoiceSegmentGate("auto");
        return !gate.ShouldDecode(Tone(0.003, 16000));
    }

    public static bool AcceptsSpeechLevelSignal()
    {
        var gate = new VRC_cantalkcn.Services.SenseVoiceSegmentGate("auto");
        return gate.ShouldDecode(Tone(0.08, 16000));
    }

    public static bool AdaptsToRaisedBackground()
    {
        var gate = new VRC_cantalkcn.Services.SenseVoiceSegmentGate("auto");
        var borderline = Tone(0.01, 16000);
        if (!gate.ShouldDecode(borderline)) return false;
        var background = Tone(0.004, 512);
        for (var i = 0; i < 200; i++) gate.ObserveBackground(background);
        return !gate.ShouldDecode(borderline) && gate.ShouldDecode(Tone(0.08, 16000));
    }

    public static bool StrictIsMoreSelective()
    {
        var auto = new VRC_cantalkcn.Services.SenseVoiceSegmentGate("auto");
        var strict = new VRC_cantalkcn.Services.SenseVoiceSegmentGate("strict");
        var borderline = Tone(0.01, 16000);
        return auto.ShouldDecode(borderline) && !strict.ShouldDecode(borderline);
    }

    public static bool KeepsQuietSpeech()
    {
        var auto = new VRC_cantalkcn.Services.SenseVoiceSegmentGate("auto");
        return auto.ShouldDecode(Tone(0.0054, 16000));
    }
}
'@
Add-Type -TypeDefinition $source
if (-not [GateChecks]::RejectsLowNoise()) { throw 'Low-level noise would be decoded and sent as speech.' }
if (-not [GateChecks]::AcceptsSpeechLevelSignal()) { throw 'Speech-level signal was rejected.' }
if (-not [GateChecks]::AdaptsToRaisedBackground()) { throw 'Automatic gate did not adapt to background noise.' }
if (-not [GateChecks]::StrictIsMoreSelective()) { throw 'Strict sensitivity is not more selective.' }
if (-not [GateChecks]::KeepsQuietSpeech()) { throw 'Quiet speech would be dropped as noise.' }
Write-Output 'SenseVoice noise gate checks passed.'
