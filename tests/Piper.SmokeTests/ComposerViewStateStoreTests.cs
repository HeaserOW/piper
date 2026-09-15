using Piper.Core.Sessions;

internal static class ComposerViewStateStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("composer view state persists and is bounded", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-composer-view-{Guid.NewGuid():N}.json");
        try
        {
            runner.AreEqual(0, ComposerViewStateStore.Load(path).CollapsedHosts.Count,
                "a missing file means nothing is collapsed");

            ComposerViewStateStore.Save(new ComposerViewState
            {
                CollapsedHosts = ["api.example.test", "localhost:8080", "api.example.test"],
            }, path);

            var restored = ComposerViewStateStore.Load(path);
            runner.AreEqual(2, restored.CollapsedHosts.Count, "duplicates collapse to one entry");
            runner.IsTrue(restored.CollapsedHosts.Contains("api.example.test"), "a host round-trips");
            runner.IsTrue(restored.CollapsedHosts.Contains("localhost:8080"), "a host:port round-trips");

            // A host ultimately comes off the wire, so neither Save nor Load may carry one the
            // pane itself would never have produced.
            ComposerViewStateStore.Save(new ComposerViewState
            {
                CollapsedHosts = [new string('h', 10_000), "evil\r\nX-Injected: 1", string.Empty, "good.example.test"],
            }, path);
            var cleaned = ComposerViewStateStore.Load(path);
            runner.AreEqual(1, cleaned.CollapsedHosts.Count, "hostile hosts are dropped, the rest is kept");
            runner.AreEqual("good.example.test", cleaned.CollapsedHosts[0], "the valid host survives");

            ComposerViewStateStore.Save(new ComposerViewState
            {
                CollapsedHosts = [.. Enumerable.Range(0, ComposerViewStateStore.MaxHosts + 50).Select(i => $"host{i}.test")],
            }, path);
            runner.AreEqual(ComposerViewStateStore.MaxHosts, ComposerViewStateStore.Load(path).CollapsedHosts.Count,
                "the remembered host list is capped");

            // Load has to sanitise independently of Save: this file is plain JSON in the user's
            // profile, so it can carry whatever a hand edit -- or a build without the Save-side
            // guard -- put there. Written directly, bypassing Save, or this asserts nothing.
            File.WriteAllText(path,
                "{\"CollapsedHosts\":[\"good.example.test\",\"evil\\u000d\\u000aX-Injected: 1\",\"spoof\\u202Etset.dab\",\"\"]}");
            var loaded = ComposerViewStateStore.Load(path);
            runner.AreEqual(1, loaded.CollapsedHosts.Count, "Load drops hosts Save would never have written");
            runner.AreEqual("good.example.test", loaded.CollapsedHosts[0], "the valid host survives Load");

            File.WriteAllText(path, "not json");
            runner.AreEqual(0, ComposerViewStateStore.Load(path).CollapsedHosts.Count,
                "a corrupt file means nothing is collapsed");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
