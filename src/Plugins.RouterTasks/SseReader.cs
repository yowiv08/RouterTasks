using System.Runtime.CompilerServices;
using System.Text;

namespace Plugins.RouterTasks;

internal sealed record SseFrame(string? Event, string Data);
internal static class SseReader
{
    public static async IAsyncEnumerable<SseFrame> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        string? eventName = null; var data = new List<string>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (data.Count > 0) yield return new SseFrame(eventName, string.Join('\n', data));
                eventName = null; data.Clear(); continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") eventName = value;
            else if (field == "data") data.Add(value);
        }
        // 未由空行闭合的事件不作为正式 SSE 事件发布。
    }
}

internal sealed class ResponseLimitException() : IOException("响应超过配置的字节上限");
internal sealed class TimedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _limit;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _idle = new();
    private CancellationTokenSource? _read;
    private CancellationToken _sourceToken;
    private long _bytes;
    public TimedReadStream(Stream inner, int limit, TimeSpan idleTimeout)
    { _inner = inner; _limit = limit; _idleTimeout = idleTimeout; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_read is null || token != _sourceToken)
        {
            _read?.Dispose();
            _sourceToken = token;
            _read = CancellationTokenSource.CreateLinkedTokenSource(token, _idle.Token);
        }
        _idle.CancelAfter(_idleTimeout);
        try
        {
            var read = await _inner.ReadAsync(buffer, _read.Token);
            // 缓冲流可能同步完成且忽略 token；调用方取消仍优先于 EOF、超限或超时。
            token.ThrowIfCancellationRequested();
            if (_idle.IsCancellationRequested) throw new TimeoutException("响应流空闲超时");
            _bytes += read;
            if (_bytes > _limit) throw new ResponseLimitException();
            return read;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && _idle.IsCancellationRequested)
        { throw new TimeoutException("响应流空闲超时"); }
        finally { _idle.CancelAfter(Timeout.InfiniteTimeSpan); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _read?.Dispose(); _idle.Dispose(); }
        base.Dispose(disposing);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
