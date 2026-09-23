using System.Net;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Regressions for three ways Piper could mis-frame a response it forwards. Each one fails against
// the code immediately before this suite landed.
internal static class FramingCorrectnessTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a HEAD reply keeps the size the origin reported", async () =>
        {
            // A downloader that asks HEAD for a file's size before fetching it gets that size from
            // Content-Length. Piper used to overwrite it with the length of the body it did not
            // read -- zero -- so the file looked empty.
            await using var origin = new TestRawOrigin(async (head, stream, ct) =>
            {
                if (head.StartsWith("HEAD", StringComparison.Ordinal))
                    await TestRawOrigin.WriteAsync(stream,
                        "HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: 987654\r\n\r\n", ct);
                else
                    await TestRawOrigin.WriteAsync(stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nafter", ct);
                return true; // keep-alive, so the next request proves the connection is still sane
            });

            using var harness = new ProxyHarness();
            using var client = harness.CreateClient();
            var url = $"http://127.0.0.1:{origin.Port}";

            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{url}/pack.zip"));
            runner.AreEqual(HttpStatusCode.OK, head.StatusCode, "HEAD succeeds");
            runner.AreEqual(987654L, head.Content.Headers.ContentLength ?? -1,
                "the origin's Content-Length reaches the client untouched");
            runner.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length, "and no body is sent");

            // The desync detector: if the HEAD reply had been framed wrongly, this either hangs or
            // parses the wrong bytes.
            var next = await client.GetAsync($"{url}/after");
            runner.AreEqual(HttpStatusCode.OK, next.StatusCode, "a later request on the connection still works");
            runner.AreEqual("after", await next.Content.ReadAsStringAsync(), "and gets its own body");
        });

        await runner.RunAsync("a close-delimited body at the cap is refused, not quietly cut short", () =>
        {
            // Returning a short buffer here is worse than failing: the caller advertises the
            // truncated length downstream while the rest is still queued on the socket, so the next
            // message read from that connection starts mid-body.
            runner.IsTrue(Throws(() => ReadToEnd(new byte[1000], limit: 500)),
                "a body past the cap throws");
            runner.IsTrue(!Throws(() => ReadToEnd(new byte[500], limit: 500)),
                "a body exactly at the cap is fine");
            runner.IsTrue(!Throws(() => ReadToEnd(new byte[499], limit: 500)),
                "a body under the cap is fine");
            runner.AreEqual(499, ReadToEnd(new byte[499], limit: 500).Length, "and comes back whole");

            return Task.CompletedTask;
        });

        await runner.RunAsync("101 Switching Protocols is relayed, not swallowed as interim", async () =>
        {
            // 101 sits in the 1xx range but is final: it hands the connection to another protocol.
            // Piper treated it as interim and kept reading HTTP off a socket that had stopped
            // speaking HTTP, so every upgrade hung until it timed out.
            var parsed = await ParseResponseAsync(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
            runner.AreEqual(101, parsed.StatusCode, "the parser returns the 101 rather than reading past it");
            runner.AreEqual("websocket", parsed.Headers["Upgrade"], "with its headers intact");

            const string EarlyBytes = "ORIGIN-SPOKE-FIRST";

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                // Head and first post-upgrade bytes in one write, so they land in the proxy's read
                // buffer together. That is what makes the drain necessary rather than theoretical.
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: piper-test\r\nConnection: Upgrade\r\n\r\n"
                    + EarlyBytes, ct);

                // Then behave like the switched protocol: echo whatever the client sends.
                var buffer = new byte[64];
                var n = await stream.ReadAsync(buffer, ct);
                if (n > 0) await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                return false;
            });

            using var harness = new ProxyHarness();

            // Raw socket, because HttpClient will not hand back a connection after an upgrade.
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, harness.Port);
            var proxyStream = raw.GetStream();

            await TestRawOrigin.WriteAsync(proxyStream,
                $"GET http://127.0.0.1:{origin.Port}/ws HTTP/1.1\r\n"
                + $"Host: 127.0.0.1:{origin.Port}\r\n"
                + "Connection: Upgrade\r\nUpgrade: piper-test\r\n\r\n", CancellationToken.None);

            var relayed = await ReadAtLeastAsync(proxyStream, "\r\n\r\n".Length + EarlyBytes.Length, expect: EarlyBytes);

            runner.IsTrue(relayed.Contains("101", StringComparison.Ordinal), "the client sees the 101");
            runner.IsTrue(relayed.Contains(EarlyBytes, StringComparison.Ordinal),
                "bytes the origin sent before the client spoke are relayed, not dropped");

            await TestRawOrigin.WriteAsync(proxyStream, "CLIENT-PING", CancellationToken.None);
            var echoed = await ReadAtLeastAsync(proxyStream, "CLIENT-PING".Length, expect: "CLIENT-PING");
            runner.IsTrue(echoed.Contains("CLIENT-PING", StringComparison.Ordinal),
                "and the tunnel keeps relaying in both directions afterwards");
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A proxy listening on an ephemeral port, with its own store.</summary>
    private sealed class ProxyHarness : IDisposable
    {
        private readonly CertificateAuthority _ca;
        private readonly ProxyServer _proxy;

        public ProxyHarness()
        {
            _ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Framing-Certs"));
            Store = new SessionStore();
            _proxy = new ProxyServer(new ProxyOptions { Port = 0 }, _ca, Store);
            _proxy.Start();
            Port = _proxy.Endpoint!.Port;
        }

        public int Port { get; }

        public SessionStore Store { get; }

        public HttpClient CreateClient() => new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port}", BypassOnLocal: false),
            UseProxy = true,
        })
        { Timeout = TimeSpan.FromSeconds(20) };

        public void Dispose()
        {
            _proxy.StopAsync().GetAwaiter().GetResult();
            _ca.Dispose();
        }
    }

    private static async Task<HttpResponseData> ParseResponseAsync(string wire)
    {
        using var reader = new HttpStreamReader(new MemoryStream(Encoding.Latin1.GetBytes(wire)));
        return await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
    }

    private static byte[] ReadToEnd(byte[] content, long limit)
    {
        using var reader = new HttpStreamReader(new MemoryStream(content));
        return reader.ReadToEndAsync(limit, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (HttpParseException)
        {
            return true;
        }
    }

    /// <summary>Reads until <paramref name="expect"/> has shown up, or the read budget runs out.</summary>
    private static async Task<string> ReadAtLeastAsync(NetworkStream stream, int minimum, string expect)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        var buffer = new byte[1024];
        while (text.Length < minimum || !text.ToString().Contains(expect, StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(buffer, timeout.Token);
            if (n == 0) break;
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return text.ToString();
    }
}
