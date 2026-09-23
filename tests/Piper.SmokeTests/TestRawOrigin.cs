using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// An origin server that writes whatever bytes a test tells it to.
/// </summary>
/// <remarks>
/// <c>HttpListener</c> cannot express the cases that matter here: a HEAD reply whose
/// Content-Length describes a body it does not send, a response with no framing at all, a 101
/// followed immediately by bytes of another protocol, or a deliberately stalled write. All of
/// those are framing, and framing is exactly what these tests are about, so the origin has to be
/// a raw socket that emits literal wire bytes.
/// </remarks>
internal sealed class TestRawOrigin : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, NetworkStream, CancellationToken, Task<bool>> _handle;
    private int _connections;

    /// <param name="handle">
    /// Given the request head and the socket, writes the reply. Returns true to keep the
    /// connection open for another request, false to close it.
    /// </param>
    public TestRawOrigin(Func<string, NetworkStream, CancellationToken, Task<bool>> handle)
    {
        _handle = handle;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many TCP connections have been accepted. Lets a test prove that a connection
    /// was reused, or that a poisoned one was not.</summary>
    public int ConnectionCount => Volatile.Read(ref _connections);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }

            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    var head = await ReadHeadAsync(stream, _cts.Token).ConfigureAwait(false);
                    if (head is null) return;
                    if (!await _handle(head, stream, _cts.Token).ConfigureAwait(false)) return;
                }
            }
        }
        catch
        {
            // The peer went away, or the test finished. Either way this connection is done and
            // there is nobody left to report to.
        }
    }

    /// <summary>Reads up to the blank line ending the request head. Request bodies are not read;
    /// no test here sends one.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == (byte)'\r' && head[^3] == (byte)'\n'
                                && head[^2] == (byte)'\r' && head[^1] == (byte)'\n')
                return Encoding.Latin1.GetString(head.ToArray());
        }
    }

    public static Task WriteAsync(NetworkStream stream, string wire, CancellationToken ct) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(wire), ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch (SocketException) { }
        _cts.Dispose();
    }
}
