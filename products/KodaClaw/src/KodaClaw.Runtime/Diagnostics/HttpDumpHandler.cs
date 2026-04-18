using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace KodaClaw.Runtime.Diagnostics;

/// <summary>
/// Opt-in DelegatingHandler that mirrors HTTP request/response pairs to disk.
/// By default only "anomalous" exchanges are retained (non-2xx, empty body,
/// or SSE stream without any content_block event); normal responses are
/// auto-deleted on stream close to avoid noise.
///
/// Enable: set KODACLAW_HTTP_DUMP_DIR to a writable directory.
/// Force-keep everything: also set KODACLAW_HTTP_DUMP_MODE=all.
/// </summary>
public sealed class HttpDumpHandler : DelegatingHandler
{
    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "x-api-key",
        "api-key",
        "Cookie",
        "Set-Cookie",
    };

    private readonly string _dumpDir;
    private readonly bool _keepAll;
    private long _counter;

    public HttpDumpHandler(string dumpDir, bool keepAll = false)
    {
        _dumpDir = dumpDir ?? throw new ArgumentNullException(nameof(dumpDir));
        _keepAll = keepAll;
        Directory.CreateDirectory(_dumpDir);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var id = BuildId();
        var reqPath = Path.Combine(_dumpDir, $"{id}.req.txt");
        var respHeadPath = Path.Combine(_dumpDir, $"{id}.resp.txt");
        var respBodyPath = Path.Combine(_dumpDir, $"{id}.resp.body");

        try
        {
            await DumpRequestAsync(request, reqPath, respBodyPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryWriteAsync(reqPath + ".error", ex.ToString()).ConfigureAwait(false);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryWriteAsync(respHeadPath, $"SEND FAILED\n{ex}").ConfigureAwait(false);
            // Send failure is always anomalous — retain the request dump and rethrow.
            throw;
        }

        var isHttpError = !response.IsSuccessStatusCode;

        try
        {
            await DumpResponseHeadersAsync(response, respHeadPath).ConfigureAwait(false);
            var paths = new DumpPaths(reqPath, respHeadPath, respBodyPath);
            response.Content = WrapResponseBody(response.Content, respBodyPath, paths, _keepAll, isHttpError);
        }
        catch (Exception ex)
        {
            await TryWriteAsync(respHeadPath + ".error", ex.ToString()).ConfigureAwait(false);
        }

        return response;
    }

    private string BuildId()
    {
        var seq = Interlocked.Increment(ref _counter);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return $"{ts}-{seq:D4}";
    }

    private static async Task DumpRequestAsync(
        HttpRequestMessage request,
        string path,
        string bodyMarker,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(1024);
        sb.Append(request.Method).Append(' ').Append(request.RequestUri).Append(" HTTP/")
          .Append(request.Version).Append('\n');
        AppendHeaders(sb, request.Headers);
        if (request.Content is not null)
        {
            AppendHeaders(sb, request.Content.Headers);
        }
        sb.Append('\n');

        if (request.Content is not null)
        {
            string body;
            try
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                body = $"<failed to read request body: {ex.Message}>";
            }
            sb.Append(body);
        }
        else
        {
            sb.Append("<no body>");
        }

        sb.Append("\n\n# response body streamed to: ").Append(bodyMarker).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task DumpResponseHeadersAsync(HttpResponseMessage response, string path)
    {
        var sb = new StringBuilder(512);
        sb.Append("HTTP/").Append(response.Version).Append(' ')
          .Append((int)response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append('\n');
        AppendHeaders(sb, response.Headers);
        if (response.Content is not null)
        {
            AppendHeaders(sb, response.Content.Headers);
        }
        await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
    }

    private static void AppendHeaders(StringBuilder sb, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            var value = RedactedHeaders.Contains(header.Key)
                ? "<redacted>"
                : string.Join(", ", header.Value);
            sb.Append(header.Key).Append(": ").Append(value).Append('\n');
        }
    }

    private static HttpContent WrapResponseBody(
        HttpContent content,
        string sinkPath,
        DumpPaths paths,
        bool keepAll,
        bool isHttpError)
    {
        if (content is null)
        {
            return content!;
        }

        var wrapped = new TeeHttpContent(content, sinkPath, paths, keepAll, isHttpError);
        foreach (var header in content.Headers)
        {
            wrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return wrapped;
    }

    private static async Task TryWriteAsync(string path, string content)
    {
        try
        {
            await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
        }
        catch
        {
            // Swallow — diagnostics must not break the call.
        }
    }

    internal static void DecideRetention(DumpPaths paths, bool keepAll, bool isHttpError, long bytes, bool sawContentBlock)
    {
        if (keepAll || isHttpError)
        {
            return;
        }

        // Heuristics for "normal" SSE response:
        //   - body >= 200 bytes (empty SSE often ≤ few bytes or zero)
        //   - saw at least one content_block_start / content_block_delta marker
        var looksNormal = bytes >= 200 && sawContentBlock;
        if (looksNormal)
        {
            TryDelete(paths.RequestPath);
            TryDelete(paths.ResponseHeadersPath);
            TryDelete(paths.ResponseBodyPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}

internal readonly record struct DumpPaths(string RequestPath, string ResponseHeadersPath, string ResponseBodyPath);

/// <summary>
/// Wraps an HttpContent so its response body is duplicated to disk while still being
/// consumable by the caller (IAsyncEnumerable SSE path, ReadAsStreamAsync, etc.).
/// </summary>
internal sealed class TeeHttpContent(
    HttpContent inner,
    string sinkPath,
    DumpPaths paths,
    bool keepAll,
    bool isHttpError) : HttpContent
{
    private readonly HttpContent _inner = inner;
    private readonly string _sinkPath = sinkPath;
    private readonly DumpPaths _paths = paths;
    private readonly bool _keepAll = keepAll;
    private readonly bool _isHttpError = isHttpError;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await using var source = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        await using var sink = new FileStream(_sinkPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var detector = new ContentBlockDetector();
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            await sink.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            total += read;
            detector.Feed(buffer, 0, read);
        }
        HttpDumpHandler.DecideRetention(_paths, _keepAll, _isHttpError, total, detector.Saw);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var inner = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        var sink = new FileStream(_sinkPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new TeeStream(inner, sink, _paths, _keepAll, _isHttpError);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Read-only stream that forwards bytes to the caller, tees them to a sink file,
/// and on close decides whether the dump triple should be retained.
/// </summary>
internal sealed class TeeStream : Stream
{
    private readonly Stream _source;
    private readonly Stream _sink;
    private readonly DumpPaths _paths;
    private readonly bool _keepAll;
    private readonly bool _isHttpError;
    private readonly ContentBlockDetector _detector = new();
    private long _total;
    private bool _decided;

    public TeeStream(Stream source, Stream sink, DumpPaths paths, bool keepAll, bool isHttpError)
    {
        _source = source;
        _sink = sink;
        _paths = paths;
        _keepAll = keepAll;
        _isHttpError = isHttpError;
    }

    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _sink.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            try { _sink.Write(buffer, offset, read); } catch { /* ignore */ }
            _total += read;
            _detector.Feed(buffer, offset, read);
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            try { await _sink.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
            _total += read;
            _detector.Feed(buffer.Span[..read]);
        }
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _source.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            try { await _sink.WriteAsync(buffer.AsMemory(offset, read), cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
            _total += read;
            _detector.Feed(buffer, offset, read);
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _sink.Flush(); } catch { /* ignore */ }
            _sink.Dispose();
            _source.Dispose();
            if (!_decided)
            {
                _decided = true;
                HttpDumpHandler.DecideRetention(_paths, _keepAll, _isHttpError, _total, _detector.Saw);
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Rolling-buffer detector for the SSE marker "content_block". Robust against
/// chunk boundaries by carrying the last ~32 bytes across feeds.
/// </summary>
internal sealed class ContentBlockDetector
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("content_block");
    private readonly byte[] _tail = new byte[Marker.Length - 1];
    private int _tailLen;
    public bool Saw { get; private set; }

    public void Feed(byte[] buffer, int offset, int count)
        => Feed(new ReadOnlySpan<byte>(buffer, offset, count));

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (Saw || chunk.IsEmpty)
        {
            return;
        }

        // Combine carried tail + new chunk prefix and scan across the seam.
        if (_tailLen > 0)
        {
            var prefixLen = Math.Min(chunk.Length, 256);
            Span<byte> joined = stackalloc byte[_tailLen + prefixLen];
            _tail.AsSpan(0, _tailLen).CopyTo(joined);
            chunk[..prefixLen].CopyTo(joined[_tailLen..]);
            if (joined.IndexOf(Marker) >= 0)
            {
                Saw = true;
                return;
            }
        }

        if (chunk.IndexOf(Marker) >= 0)
        {
            Saw = true;
            return;
        }

        var carry = Math.Min(_tail.Length, chunk.Length);
        chunk[^carry..].CopyTo(_tail);
        _tailLen = carry;
    }
}
