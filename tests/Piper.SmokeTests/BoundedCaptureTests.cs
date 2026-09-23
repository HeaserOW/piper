using System.Net;
using System.Security.Cryptography;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Keeping only part of a large body, and saying so everywhere a size or a body is reported.
internal static class BoundedCaptureTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a body past the capture limit is relayed whole but kept in part", async () =>
        {
            var payload = RandomNumberGenerator.GetBytes(256 * 1024);
            var expected = Convert.ToHexString(SHA256.HashData(payload));
            const int Keep = 16 * 1024;

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {payload.Length}\r\n\r\n", ct);
                await stream.WriteAsync(payload, ct);
                return false;
            });

            using var harness = new ProxyHarness(o => o.MaxCapturedBodyBytes = Keep);
            using var client = harness.CreateClient();

            var got = await client.GetByteArrayAsync($"http://127.0.0.1:{origin.Port}/big.zip");

            // The client is the part that must not be affected by a capture decision.
            runner.AreEqual(payload.Length, got.Length, "the client still receives every byte");
            runner.AreEqual(expected, Convert.ToHexString(SHA256.HashData(got)), "unaltered");

            var session = harness.Store.Snapshot().Last(s => s.Url.Contains("big.zip", StringComparison.Ordinal));
            runner.AreEqual(Keep, session.Response!.Body.Length, "only the limit is retained");
            runner.AreEqual((long)payload.Length, session.Response.BodyTotalLength,
                "while the size reported is what crossed the wire");
            runner.AreEqual((long)payload.Length, session.ResponseSize, "which is what the grid shows");
            runner.IsTrue(!session.Response.IsBodyComplete, "and the capture is marked incomplete");
            runner.AreEqual(Convert.ToHexString(SHA256.HashData(payload[..Keep])),
                Convert.ToHexString(SHA256.HashData(session.Response.Body)),
                "the retained part is the start of the body, byte-exact");
        });

        await runner.RunAsync("a body within the limit is captured whole and not flagged", async () =>
        {
            // The negative control: without it, a flag stuck on would look like a passing test.
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nsmall", ct);
                return false;
            });

            using var harness = new ProxyHarness(o => o.MaxCapturedBodyBytes = 16 * 1024);
            using var client = harness.CreateClient();

            runner.AreEqual("small", await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/small.txt"),
                "the body arrives");

            var session = harness.Store.Snapshot().Last(s => s.Url.Contains("small.txt", StringComparison.Ordinal));
            runner.IsTrue(session.Response!.IsBodyComplete, "a small body is complete");
            runner.AreEqual(5L, session.ResponseSize, "and reports its own size");
        });

        await runner.RunAsync("the store bounds how many body bytes it keeps", () =>
        {
            // Asserted over bytes computed from the sessions themselves. Process memory would be a
            // reading of the garbage collector's mood, not of what the store is holding on to.
            var store = new SessionStore { RetainedBodyBudgetBytes = 200_000 };

            for (var i = 0; i < 40; i++)
            {
                var response = new HttpResponseData { Body = new byte[20_000] };
                response.BodyTotalLength = 20_000;
                store.Add(new Session
                {
                    Request = new HttpRequestData { RequestTarget = $"/file-{i}" },
                    Response = response,
                    State = SessionState.Complete,
                    Completed = DateTimeOffset.Now,
                });
            }

            var all = store.Snapshot();
            runner.AreEqual(40, all.Length, "every session is kept -- only bodies are released");
            runner.IsTrue(all.Sum(s => s.Response!.Body.LongLength) <= 200_000,
                $"retained bytes stay within the budget (held {all.Sum(s => s.Response!.Body.LongLength)})");

            runner.AreEqual(0, all[0].Response!.Body.Length, "the oldest body is the one released");
            runner.IsTrue(all[^1].Response!.Body.Length > 0, "while the newest is still there to look at");

            // The important part: a released body must not start reading as an empty one.
            runner.AreEqual(20_000L, all[0].Response!.BodyTotalLength, "a released body keeps its true size");
            runner.AreEqual(20_000L, all[0].ResponseSize, "so the grid still reports what was transferred");
            runner.AreEqual("/file-0", all[0].Request!.RequestTarget, "and the session itself survives");

            return Task.CompletedTask;
        });

        await runner.RunAsync("an exported archive does not pass a fragment off as a whole body", () =>
        {
            var partial = new HttpResponseData { Body = Encoding.Latin1.GetBytes("first-half") };
            partial.Headers.Set("Content-Length", "99999");
            partial.BodyTotalLength = 99_999;

            var session = new Session
            {
                Request = new HttpRequestData { Url = new Uri("http://example.test/big"), RequestTarget = "/big" },
                Response = partial,
                State = SessionState.Complete,
                Completed = DateTimeOffset.Now,
            };

            var path = Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-Truncated-{Guid.NewGuid():N}.saz");
            try
            {
                SazExporter.Export(path, [session]);
                var reimported = SazImporter.Import(path);

                runner.AreEqual(1, reimported.Sessions.Count, "the archive round-trips");
                var exported = reimported.Sessions[0].Response!;

                runner.AreEqual("10", exported.Headers["Content-Length"],
                    "the archive describes the bytes it actually contains");
                runner.AreEqual("99999", exported.Headers["X-Piper-Body-Truncated"],
                    "and records the length the body really had");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }

            return Task.CompletedTask;
        });
    }

    private sealed class ProxyHarness : IDisposable
    {
        private readonly CertificateAuthority _ca;
        private readonly ProxyServer _proxy;

        public ProxyHarness(Action<ProxyOptions>? configure = null)
        {
            _ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Capture-Certs"));
            Store = new SessionStore();
            var options = new ProxyOptions { Port = 0 };
            configure?.Invoke(options);
            _proxy = new ProxyServer(options, _ca, Store);
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
        { Timeout = TimeSpan.FromSeconds(30) };

        public void Dispose()
        {
            _proxy.StopAsync().GetAwaiter().GetResult();
            _ca.Dispose();
        }
    }
}
