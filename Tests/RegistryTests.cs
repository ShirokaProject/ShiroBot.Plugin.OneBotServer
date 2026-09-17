using ShiroBot.Plugin.OneBotServer.Infrastructure;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class RegistryTests
{
    [TestMethod]
    public async Task MessageIdRegistry_PersistsConcurrentRegistrationAndEvictsOldestEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "ids.json");
            var registry = await MessageIdRegistry.OpenAsync(path, 2);
            var reference = new MessageReference(MessageScene.Group, 10, 20);
            var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => registry.RegisterAsync(reference)));
            Assert.IsTrue(ids.All(id => id == 1));
            await registry.RegisterAsync(new(MessageScene.Group, 10, 21));
            await registry.RegisterAsync(new(MessageScene.Group, 10, 22));
            Assert.AreEqual(2, registry.Count);
            Assert.IsFalse(registry.TryResolve(1, out _));
            var reopened = await MessageIdRegistry.OpenAsync(path, 2);
            Assert.IsTrue(reopened.TryResolve(3, out var persisted));
            Assert.AreEqual(22L, persisted!.Sequence);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
