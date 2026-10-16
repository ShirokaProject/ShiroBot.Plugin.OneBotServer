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
            var reference = new OneBotMessageReference(MessageScene.Group, 10, 20);
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

    [TestMethod]
    public async Task MessageIdRegistry_FailedReplacementPreservesStateAndNextId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "ids.json");
            var registry = await MessageIdRegistry.OpenAsync(path, 1);
            var first = new OneBotMessageReference(MessageScene.Group, 10, 20);
            var second = new OneBotMessageReference(MessageScene.Group, 10, 21);
            Assert.AreEqual(1, await registry.RegisterAsync(first));
            File.Move(path, path + ".saved");
            Directory.CreateDirectory(path);
            try
            {
                await Assert.ThrowsExceptionAsync<IOException>(() => registry.RegisterAsync(second));
                Assert.AreEqual(1, registry.Count);
                Assert.IsTrue(registry.TryResolve(1, out var retained));
                Assert.AreEqual(first, retained);
                Assert.IsFalse(registry.TryGet(second, out _));
                Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
            }
            finally
            {
                Directory.Delete(path);
                File.Move(path + ".saved", path);
            }
            Assert.AreEqual(2, await registry.RegisterAsync(second));
            var reopened = await MessageIdRegistry.OpenAsync(path, 1);
            Assert.IsFalse(reopened.TryResolve(1, out _));
            Assert.IsTrue(reopened.TryResolve(2, out var persisted));
            Assert.AreEqual(second, persisted);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task MessageIdRegistry_CanceledRegistrationPreservesFileAndEvictionOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "ids.json");
            var registry = await MessageIdRegistry.OpenAsync(path, 1);
            var first = new OneBotMessageReference(MessageScene.Group, 10, 20);
            var second = new OneBotMessageReference(MessageScene.Group, 10, 21);
            await registry.RegisterAsync(first);
            var original = await File.ReadAllBytesAsync(path);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => registry.RegisterAsync(second, canceled.Token));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
            Assert.IsTrue(registry.TryResolve(1, out var retained));
            Assert.AreEqual(first, retained);
            Assert.IsFalse(registry.TryGet(second, out _));
            Assert.AreEqual(2, await registry.RegisterAsync(second));
            Assert.IsFalse(registry.TryResolve(1, out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
