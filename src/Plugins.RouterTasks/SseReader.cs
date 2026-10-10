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
        while (await reader.ReadLineAsync(token) is { } line)
        {
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
internal sealed class TimedReadStream(Stream inner, int limit, TimeSpan idleTimeout) : Stream
{
    private long _bytes;
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
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(idleTimeout);
        int read;
        try { read = await inner.ReadAsync(buffer, idle.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("响应流空闲超时"); }
        _bytes += read;
        if (_bytes > limit) throw new ResponseLimitException();
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
