using System.Net.Http;
using System.Security.Cryptography;

namespace VRC_cantalkcn.Services;

public static class SenseVoiceModelService
{
    public static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ucantalk", "models", "sensevoice-small");
    private const string ModelUrl = "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/2365baeacb507f821a0c8120fcee3d484dba7a07/";
    private static readonly (string Name, string Url, long Size, string Hash)[] Files =
    [
        ("model.int8.onnx", ModelUrl + "model.int8.onnx", 239233841, "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51"),
        ("tokens.txt", ModelUrl + "tokens.txt", 315894, "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc"),
        ("silero_vad.onnx", "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", 643854, "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6")
    ];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };
    public static bool IsInstalled => Files.All(IsValid);
    public static bool IsVadInstalled => IsValid(Files[2]);
    public static (string Model, string Tokens, string Vad) ResolveFiles(string? customDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(customDirectory) ? ModelDirectory : customDirectory.Trim();
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"SenseVoice 模型目录不存在：{directory}");
        var int8 = Path.Combine(directory, "model.int8.onnx");
        var full = Path.Combine(directory, "model.onnx");
        var model = File.Exists(int8) ? int8 : full;
        var tokens = Path.Combine(directory, "tokens.txt");
        if (!File.Exists(model) || !File.Exists(tokens))
            throw new InvalidDataException("SenseVoice 目录需包含 model.int8.onnx（或 model.onnx）和 tokens.txt；原始 FunASR 的 tokens.json 不能直接用于 Sherpa。");
        var localVad = Path.Combine(directory, "silero_vad.onnx");
        var vad = File.Exists(localVad) ? localVad : Path.Combine(ModelDirectory, "silero_vad.onnx");
        if (!File.Exists(vad)) throw new InvalidDataException("SenseVoice 缺少 Silero VAD 文件，请先下载辅助文件。");
        return (model, tokens, vad);
    }
    private static bool IsValid((string Name, string Url, long Size, string Hash) file)
    {
        var path = Path.Combine(ModelDirectory, file.Name);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Hash, StringComparison.OrdinalIgnoreCase);
    }
    public static long InstalledBytes => Directory.Exists(ModelDirectory)
        ? Directory.EnumerateFiles(ModelDirectory).Sum(p => new FileInfo(p).Length) : 0;

    public static async Task EnsureDownloadedAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => await EnsureFilesDownloadedAsync(Files, progress, cancellationToken);

    public static async Task EnsureVadDownloadedAsync(CancellationToken cancellationToken = default)
        => await EnsureFilesDownloadedAsync([Files[2]], null, cancellationToken);

    private static async Task EnsureFilesDownloadedAsync(
        IEnumerable<(string Name, string Url, long Size, string Hash)> files,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ModelDirectory);
            foreach (var file in files)
            {
                var path = Path.Combine(ModelDirectory, file.Name);
                if (IsValid(file)) continue;
                var temporary = path + ".partial";
                try
                {
                    using var response = await Client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var destination = File.Create(temporary))
                    {
                        var buffer = new byte[128 * 1024];
                        long received = 0;
                        int count;
                        var lastReport = DateTime.MinValue;
                        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                            received += count;
                            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                            {
                                progress?.Report($"正在下载 {file.Name}：{received * 100 / file.Size}%（{received / 1048576d:F1} / {file.Size / 1048576d:F1} MB）");
                                lastReport = DateTime.UtcNow;
                            }
                        }
                    }
                    await using (var downloaded = File.OpenRead(temporary))
                    {
                        var hash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
                        if (downloaded.Length != file.Size || !hash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"{file.Name} 校验失败，请重试下载。");
                    }
                    File.Move(temporary, path, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            progress?.Report("SenseVoice-Small 已下载，可以开始识别。");
        }
        finally { Gate.Release(); }
    }

    public static async Task DeleteAsync()
    {
        await Gate.WaitAsync();
        try
        {
            // Delete only this application's known model files, never a user-selected directory.
            foreach (var file in Files)
            foreach (var suffix in new[] { "", ".partial" })
            {
                var path = Path.Combine(ModelDirectory, file.Name + suffix);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        finally { Gate.Release(); }
    }
}
