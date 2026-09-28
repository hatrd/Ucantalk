$ErrorActionPreference = 'Stop'
$history = Get-Content (Join-Path $PSScriptRoot '../Services/SenseVoiceAudioHistory.cs') -Raw
$source = "using System;`n" + $history + @'

public static class OnsetChecks
{
    public static bool IncludesOneAndHalfSeconds()
    {
        var history = new VRC_cantalkcn.Services.SenseVoiceAudioHistory();
        var raw = new float[16000 * 4];
        for (var i = 0; i < raw.Length; i++) raw[i] = i;
        history.Append(raw);
        var vad = new float[16000];
        var expanded = history.WithPreRoll(16000 * 2, vad);
        return expanded.Length == 16000 * 5 / 2 &&
               expanded[0] == 16000 / 2 &&
               expanded[expanded.Length - 1] == 16000 * 3 - 1;
    }

    public static bool DoesNotRepeatPreviousUtterance()
    {
        var history = new VRC_cantalkcn.Services.SenseVoiceAudioHistory();
        var raw = new float[16000 * 5];
        for (var i = 0; i < raw.Length; i++) raw[i] = i;
        history.Append(raw);
        history.WithPreRoll(16000, new float[16000]);
        var next = history.WithPreRoll(16000 * 5 / 2, new float[16000]);
        return next[0] == 16000 * 2;
    }

    public static bool ResetClearsOldAudio()
    {
        var history = new VRC_cantalkcn.Services.SenseVoiceAudioHistory();
        history.Append(new float[16000 * 3]);
        history.Reset();
        var fresh = new float[16000 * 2];
        for (var i = 0; i < fresh.Length; i++) fresh[i] = 7;
        history.Append(fresh);
        var expanded = history.WithPreRoll(16000, new float[16000]);
        return expanded.Length == fresh.Length && expanded[0] == 7;
    }
}
'@
Add-Type -TypeDefinition $source
if (-not [OnsetChecks]::IncludesOneAndHalfSeconds()) { throw 'The quiet onset was not included.' }
if (-not [OnsetChecks]::DoesNotRepeatPreviousUtterance()) { throw 'Previous speech leaked into the next segment.' }
if (-not [OnsetChecks]::ResetClearsOldAudio()) { throw 'Suppression reset did not clear old audio.' }
Write-Output 'SenseVoice onset history checks passed.'
