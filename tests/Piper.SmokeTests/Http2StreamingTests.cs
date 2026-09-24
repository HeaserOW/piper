using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http2;

// The HTTP/2 side of relaying a body: DATA frames go out as the bytes arrive rather than once the
// whole body is in hand, and a sender that runs out of send window resumes when the peer grants
// more.
//
// Cleartext h2c over loopback, no TLS and no upstream, so what is being tested is the connection
// itself. The ordering assertion is program order against a gate the test owns, not a stopwatch.
internal static class Http2StreamingTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await runner.RunAsync("an HTTP/2 body is framed as it arrives, not once it is whole", async () =>
        {
            var first = Encoding.Latin1.GetBytes(new string('a', 8192));
            var second = Encoding.Latin1.GetBytes(new string('b', 8192));
            var gate = new TaskCompletionSource();
            var released = false;

            await using var harness = await Harness.StartAsync((_, _) =>
            {
                var head = new HttpResponseData { StatusCode = 200 };
                head.Headers.Set("Content-Type", "application/octet-stream");

                return Task.FromResult(new Http2StreamResponse(head, async (destination, ct) =>
                {
                    await destination.WriteAsync(first, ct);
                    await destination.FlushAsync(ct);
                    await gate.Task.WaitAsync(ct);
                    await destination.WriteAsync(second, ct);
                }));
            });

            using var client = harness.CreateClient();

            var response = await client.GetAsync($"{harness.BaseUrl}/stream",
                HttpCompletionOption.ResponseHeadersRead);
            runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "the head arrives on its own");
            runner.AreEqual("2.0", response.Version.ToString(), "over h2");

            await using var body = await response.Content.ReadAsStreamAsync();
            var got = new byte[first.Length];
            await ReadExactlyAsync(body, got);

            runner.IsTrue(!released, "the first part is framed while the handler is still gated");
            runner.AreEqual(Convert.ToHexString(SHA256.HashData(first)),
                Convert.ToHexString(SHA256.HashData(got)), "and arrives byte-exact");

            released = true;
            gate.SetResult();

            var rest = new byte[second.Length];
            await ReadExactlyAsync(body, rest);
            runner.AreEqual(Convert.ToHexString(SHA256.HashData(second)),
                Convert.ToHexString(SHA256.HashData(rest)), "the remainder follows once released");
            runner.AreEqual(-1, body.ReadByte(), "and the stream ends there");
        });

        await runner.RunAsync("a body larger than the flow-control window is sent whole after stalling", async () =>
        {
            // Comfortably larger than the 65,535-byte connection window a peer starts with, so the
            // send window is certain to run out and be replenished many times over.
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
            var expected = Convert.ToHexString(SHA256.HashData(payload));
            Http2Connection? served = null;

            await using var harness = await Harness.StartAsync((_, _) =>
            {
                var head = new HttpResponseData { StatusCode = 200, Body = payload };
                head.Headers.Set("Content-Type", "application/octet-stream");
                return Task.FromResult<Http2StreamResponse>(head);
            }, connection => served = connection);

            using var client = harness.CreateClient();

            var got = await client.GetByteArrayAsync($"{harness.BaseUrl}/large");

            runner.AreEqual(payload.Length, got.Length, "the whole body arrives");
            runner.AreEqual(expected, Convert.ToHexString(SHA256.HashData(got)), "unaltered");

            // Without this the test would pass just as happily if the window were never exhausted,
            // proving nothing about the waiting path at all. It asserts that the sender did stall
            // and did resume -- not that it was woken by the grant rather than by a timer, which
            // is a timing property and any threshold tight enough to catch a regression in it
            // would be flaky on a loaded machine.
            runner.IsTrue(served is not null && served.WindowStalls > 0,
                $"the sender ran out of window and resumed ({served?.WindowStalls} stalls)");
        });

        await runner.RunAsync("a relay that fails part-way resets the stream instead of ending it", async () =>
        {
            // The head is already out, so only RST_STREAM can still say the body is incomplete. An
            // END_STREAM here would hand the client a short download that looks like a finished one.
            await using var harness = await Harness.StartAsync((_, _) =>
            {
                var head = new HttpResponseData { StatusCode = 200 };
                return Task.FromResult(new Http2StreamResponse(head, async (destination, ct) =>
                {
                    await destination.WriteAsync("partial"u8.ToArray(), ct);
                    await destination.FlushAsync(ct);
                    throw new IOException("origin went away");
                }));
            });

            using var client = harness.CreateClient();

            string outcome;
            try
            {
                var got = await client.GetByteArrayAsync($"{harness.BaseUrl}/cut");
                outcome = $"completed with {got.Length} bytes";
            }
            catch (HttpRequestException) { outcome = "failed"; }
            catch (IOException) { outcome = "failed"; }

            runner.AreEqual("failed", outcome, "the client sees the transfer fail");
        });

        await runner.RunAsync("a head that cannot be sent still runs the relay, cancelled, and resets the stream", async () =>
        {
            // The relay owns wherever its body comes from -- the proxy's upstream connection -- so it
            // has to run even when the head never went out, or that connection is never released.
            var relayRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var harness = await Harness.StartAsync((_, _) =>
            {
                var head = new HttpResponseData { StatusCode = 200 };
                head.Headers.Add("X-Unencodable", null!); // HPACK has no representation for it
                return Task.FromResult(new Http2StreamResponse(head, (_, ct) =>
                {
                    relayRan.TrySetResult(ct.IsCancellationRequested);
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }));
            });

            using var client = harness.CreateClient();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            string outcome;
            try
            {
                using var response = await client.GetAsync($"{harness.BaseUrl}/unencodable", budget.Token);
                outcome = $"answered {(int)response.StatusCode}";
            }
            catch (HttpRequestException) { outcome = "failed"; }
            catch (OperationCanceledException) { outcome = "left waiting"; }

            runner.AreEqual("failed", outcome, "the client is told the stream failed");
            var ran = await Task.WhenAny(relayRan.Task, Task.Delay(TimeSpan.FromSeconds(10))) == relayRan.Task;
            runner.IsTrue(ran && relayRan.Task.Result, "the relay ran, with a token already cancelled");
        });

        await runner.RunAsync("a raised SETTINGS_INITIAL_WINDOW_SIZE unblocks a stream that had none", async () =>
        {
            // RFC 9113 6.9.2: a new initial window moves every open stream's window by the
            // difference. The stream here opens with a window of zero and is only ever given more
            // by that SETTINGS change -- no WINDOW_UPDATE is sent -- so the body can arrive only if
            // the change is applied and the waiting sender is woken by it.
            await using var harness = await Harness.StartAsync((_, _) =>
                Task.FromResult<Http2StreamResponse>(new HttpResponseData { StatusCode = 200, Body = "hello"u8.ToArray() }));

            using var tcp = new TcpClient();
            var url = new Uri($"{harness.BaseUrl}/");
            await tcp.ConnectAsync(IPAddress.Loopback, url.Port);
            var wire = tcp.GetStream();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await wire.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), budget.Token);
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Settings, Http2FrameFlags.None, 0, InitialWindow(0), budget.Token);

            var request = new HttpRequestData { Method = "GET", RequestTarget = "/", Url = url, HttpVersion = "HTTP/2" };
            var block = Piper.Core.Http2.Hpack.HpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(request));
            await Http2FrameWriter.WriteHeadersAsync(wire, 1, block, endStream: true, 16_384, budget.Token);
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Settings, Http2FrameFlags.None, 0, InitialWindow(65_535), budget.Token);

            var body = new List<byte>();
            try
            {
                while (true)
                {
                    var frame = await Http2FrameReader.ReadRequiredAsync(wire, 16_384, budget.Token);
                    if (frame.Type != Http2FrameType.Data || frame.StreamId != 1) continue;
                    body.AddRange(frame.DataPayload.ToArray());
                    if (frame.HasFlag(Http2FrameFlags.EndStream)) break;
                }
            }
            catch (OperationCanceledException) { /* reported below as a missing body */ }

            runner.AreEqual("hello", Encoding.Latin1.GetString(body.ToArray()), "the body is sent once the window opens");
        });
    }

    private static byte[] InitialWindow(uint size) =>
        [0, 4, (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size];

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private Harness(TcpListener listener,
            Func<HttpRequestData, CancellationToken, Task<Http2StreamResponse>> handler,
            Action<Http2Connection>? observe)
        {
            _listener = listener;
            _acceptLoop = AcceptLoopAsync(handler, observe);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public static Task<Harness> StartAsync(
            Func<HttpRequestData, CancellationToken, Task<Http2StreamResponse>> handler,
            Action<Http2Connection>? observe = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new Harness(listener, handler, observe));
        }

        public HttpClient CreateClient() => new()
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };

        private async Task AcceptLoopAsync(
            Func<HttpRequestData, CancellationToken, Task<Http2StreamResponse>> handler,
            Action<Http2Connection>? observe)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using var owned = client;
                    owned.NoDelay = true;
                    var connection = new Http2Connection(owned.GetStream(), handler);
                    observe?.Invoke(connection);
                    try { await connection.RunAsync(_cts.Token).ConfigureAwait(false); }
                    catch { /* the test asserts on the client side */ }
                }, _cts.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch (SocketException) { }
            _cts.Dispose();
        }
    }

    private static async Task ReadExactlyAsync(Stream source, byte[] destination)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var offset = 0;
        while (offset < destination.Length)
        {
            var n = await source.ReadAsync(destination.AsMemory(offset), budget.Token);
            if (n == 0) throw new IOException($"stream ended after {offset} of {destination.Length} bytes");
            offset += n;
        }
    }
}
