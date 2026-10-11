using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class LoopbackTests
{
    private static void UseSockets(Fixture f) => f.Http.Setup(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
        .Returns((PluginHttpClientOptions options) => new HttpClient(new SocketsHttpHandler
        { AllowAutoRedirect = options.AllowAutoRedirect, UseProxy = false, UseCookies = false }) { Timeout = options.RequestTimeout });

    [TestMethod]
    public async Task RealTcpSseKeepsAliveThroughShortBusyResponseAndCancelsAllConnections()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = new ConcurrentQueue<LoopbackRequest>();
        await using var server = new LoopbackServer(async (n, request, stream, token) =>
        {
            captures.Enqueue(request);
            if (n is 1 or 3) { await LoopbackServer.Send(stream, 500, "queued", "text/plain", token); return; }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"), token);
            if (n < 5)
            {
                var frame = Encoding.UTF8.GetBytes(": 心跳不是接受\r\n\r\nevent: response.created\r\ndata: {\"hello\":\"中文\"}\r\n\r\n");
                // 故意从任意字节切开，包括 UTF-8 及 CRLF。
                foreach (var b in frame) await stream.WriteAsync(new byte[] { b }, token);
            }
            else blocked.TrySetResult();
            var buffer = new byte[1];
            try { if (await stream.ReadAsync(buffer, token) == 0 && n == 5) disconnected.TrySetResult(); }
            catch (IOException) when (n == 5) { disconnected.TrySetResult(); }
        });
        await using var f = new Fixture(); UseSockets(f); f.Site.BaseUrl = server.Origin;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var task = await f.TaskAsync(); Assert.AreEqual(1, task.Runtime.QueueRound); Assert.AreEqual(0L, task.Runtime.Recoveries);
        Assert.AreEqual("keepalive", task.Runtime.Phase); Assert.AreEqual(5L, task.Runtime.TotalAttempts);
        await f.Plugin.Engine.ActionAsync("task", "pause"); await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, f.Plugin.Engine.ActiveCount); Assert.AreEqual(5, captures.Count);
        Assert.IsTrue(captures.All(r => r.Method == "POST" && r.Path == "/v1/responses"));
        Assert.IsTrue(captures.All(r => r.Headers["Authorization"] == "Bearer TOKEN-secret-test-only"));
        Assert.IsTrue(captures.All(r => JsonNode.Parse(r.Body)!["stream"]!.GetValue<bool>()));
    }

    [TestMethod]
    public async Task RealRedirectNeverReceivesCredentialsAtTheTarget()
    {
        var redirected = 0;
        await using var target = new LoopbackServer(async (_, _, stream, token) => { Interlocked.Increment(ref redirected); await LoopbackServer.Send(stream, 200, "{}", "application/json", token); });
        await using var server = new LoopbackServer(async (_, _, stream, token) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: {target.Origin}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
        });
        await using var f = new Fixture(); UseSockets(f); f.Site.BaseUrl = server.Origin; await f.SeedAsync();
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), false, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(ProbeKind.Permanent, result.Kind); Assert.AreEqual(307, result.Status); Assert.AreEqual(0, redirected);
    }
}

internal sealed record LoopbackRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _work;
    public string Origin { get; }
    public LoopbackServer(Func<int, LoopbackRequest, NetworkStream, CancellationToken, Task> handle)
    {
        _listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        _work = Serve();
        async Task Serve()
        {
            var count = 0;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(One(socket, ++count));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        async Task One(TcpClient socket, int n)
        {
            using (socket)
            {
                try
                {
                    using var stream = socket.GetStream(); var bytes = new List<byte>(); var single = new byte[1];
                    while (bytes.Count < 64 * 1024)
                    {
                        if (await stream.ReadAsync(single, _stop.Token) == 0) return;
                        bytes.Add(single[0]);
                        if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1).Where(l => l.Contains(':')))
                    { var colon = line.IndexOf(':'); headers[line[..colon]] = line[(colon + 1)..].Trim(); }
                    var length = int.TryParse(headers.GetValueOrDefault("Content-Length"), out var size) ? size : 0;
                    if (length > 1024 * 1024) throw new IOException("Loopback request too big");
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, _stop.Token);
                    await handle(n, new LoopbackRequest(first[0], first[1], headers, Encoding.UTF8.GetString(body)), stream, _stop.Token);
                }
                catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or IOException) { }
            }
        }
    }
    public static async Task Send(NetworkStream stream, int status, string body, string type, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
        await stream.WriteAsync(bytes, token);
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop(); await _work; await Task.WhenAll(_connections); _stop.Dispose();
    }
}
