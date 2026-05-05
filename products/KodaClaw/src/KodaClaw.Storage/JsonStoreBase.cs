using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Storage;

/// <summary>
/// 文件存储基类，提供三种核心原语：
/// 1. WriteEntityAsync / ReadEntityAsync — WAL 原子写（参考 JsonAgentStore.WriteWithWalAsync）
/// 2. AppendLineAsync / ReadLastLinesAsync — JSONL 追加日志（参考 JsonAgentStore.AppendEventAsync）
/// 3. ScanDirectoryAsync — 目录扫描 + 内存 LINQ 过滤
/// </summary>
public abstract class JsonStoreBase
{
    protected readonly JsonSerializerOptions JsonOptions;
    protected readonly ILogger? Logger;

    // 用于 JSONL 单行序列化（不缩进）
    private readonly JsonSerializerOptions _compactOptions;

    protected JsonStoreBase(ILogger? logger = null)
    {
        Logger = logger;
        JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        _compactOptions = new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
    }

    // ─── 原子写入 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 原子写入实体到 JSON 文件。先写唯一临时文件，再 File.Move(overwrite:true) 落地。
    /// 使用唯一临时文件名避免并发写入时的竞态（不同于共享 .wal 文件名）。
    /// </summary>
    protected async Task WriteEntityAsync<T>(string path, T value, CancellationToken ct)
    {
        EnsureDirectory(path);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true); // 原子覆盖，并发安全
    }

    /// <summary>
    /// 读取 JSON 文件。
    /// </summary>
    protected async Task<T?> ReadEntityAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to deserialize entity from {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// 删除实体文件。
    /// </summary>
    protected static Task DeleteEntityAsync(string path, CancellationToken _)
    {
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    // ─── 目录扫描 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 扫描目录下所有 {id}.json，反序列化后在内存中过滤。
    /// </summary>
    protected async Task<List<T>> ScanDirectoryAsync<T>(
        string dir,
        Func<T, bool>? predicate,
        CancellationToken ct) where T : class
    {
        if (!Directory.Exists(dir)) return [];

        var result = new List<T>();
        foreach (var file in Directory.GetFiles(dir, "*.json")
            .Where(f => string.Equals(
                Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var item = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (item != null && (predicate == null || predicate(item)))
                    result.Add(item);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Skipping corrupted file {File}", file);
            }
        }
        return result;
    }

    // ─── JSONL 追加日志 ────────────────────────────────────────────────────────

    /// <summary>
    /// 追加一行到 JSONL 文件。使用 FileShare.ReadWrite + 指数退避 retry。
    /// </summary>
    protected async Task AppendLineAsync<T>(string path, T value, CancellationToken ct)
    {
        EnsureDirectory(path);
        var line = JsonSerializer.Serialize(value, _compactOptions);

        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);
                await using var writer = new StreamWriter(stream, leaveOpen: false);
                await writer.WriteLineAsync(line.AsMemory(), ct);
                await writer.FlushAsync(ct);
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }
        throw new IOException($"Failed to append to {path} after {maxRetries} attempts");
    }

    /// <summary>
    /// 读取 JSONL 文件最后 limit 行（逆序读，再还原时间顺序）。
    /// </summary>
    protected async Task<List<T>> ReadLastLinesAsync<T>(
        string path,
        int limit,
        CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return [];

        var lines = new List<string>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }

        var result = new List<T>(Math.Min(limit, lines.Count));
        for (int i = lines.Count - 1; i >= 0 && result.Count < limit; i--)
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(lines[i], JsonOptions);
                if (item != null) result.Add(item);
            }
            catch (Exception ex) { Logger?.LogWarning(ex, "Skipping corrupted JSONL line in {Path}", path); }
        }
        result.Reverse(); // 恢复时间顺序
        return result;
    }

    /// <summary>
    /// 读取 JSONL 文件全部行并反序列化。
    /// </summary>
    protected async Task<List<T>> ReadAllLinesAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return [];

        var result = new List<T>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (item != null) result.Add(item);
            }
            catch (Exception ex) { Logger?.LogWarning(ex, "Skipping corrupted JSONL line in {Path}", path); }
        }
        return result;
    }

    /// <summary>
    /// JSONL 文件行数超过 maxLines 时，用 WAL 截断保留最新 keepLines 行。
    /// </summary>
    protected async Task TrimLogFileAsync(
        string path,
        int maxLines,
        int keepLines,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return;

        var lines = (await File.ReadAllLinesAsync(path, ct))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length <= maxLines) return;

        var kept = lines[^keepLines..];
        var walPath = path + ".wal";
        await File.WriteAllLinesAsync(walPath, kept, ct);
        if (File.Exists(path)) File.Delete(path);
        File.Move(walPath, path);
    }

    // ─── 工具方法 ──────────────────────────────────────────────────────────────

    protected static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

}
