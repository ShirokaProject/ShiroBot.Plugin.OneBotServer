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
            using var registry = await MessageIdRegistry.OpenAsync(path, 2);
            var reference = new OneBotMessageReference(MessageScene.Group, 10, 20);
            using var secondConnection = await MessageIdRegistry.OpenAsync(path, 2);
            var ids = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(i => Task.Run(() => (i % 2 == 0 ? registry : secondConnection).RegisterAsync(reference))));
            Assert.IsTrue(ids.All(id => id == 1));
            await registry.RegisterAsync(new(MessageScene.Group, 10, 21));
            await registry.RegisterAsync(new(MessageScene.Group, 10, 22));
            Assert.AreEqual(2, registry.Count);
            Assert.IsFalse(registry.TryResolve(1, out _));
            using var reopened = await MessageIdRegistry.OpenAsync(path, 2);
            Assert.IsTrue(reopened.TryResolve(3, out var persisted));
            Assert.AreEqual(22L, persisted!.Sequence);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task MessageIdRegistry_FailedTransactionPreservesStateAndNextId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "ids.json");
            using var registry = await MessageIdRegistry.OpenAsync(path, 1);
            var first = new OneBotMessageReference(MessageScene.Group, 10, 20);
            var second = new OneBotMessageReference(MessageScene.Group, 10, 21);
            Assert.AreEqual(1, await registry.RegisterAsync(first));
            using var database = MessageIdDatabase.Open(MessageIdRegistry.GetDatabasePath(path));
            database.ExecuteScript("""
                CREATE TRIGGER reject_cursor_update BEFORE UPDATE ON registry_state
                BEGIN SELECT raise(ABORT, 'test failure after insertion and eviction'); END;
                """);
            await Assert.ThrowsExceptionAsync<IOException>(() => registry.RegisterAsync(second));
            Assert.AreEqual(1, registry.Count);
            Assert.IsTrue(registry.TryResolve(1, out var retained));
            Assert.AreEqual(first, retained);
            Assert.IsFalse(registry.TryGet(second, out _));
            database.ExecuteScript("DROP TRIGGER reject_cursor_update");
            Assert.AreEqual(2, await registry.RegisterAsync(second));
            using var reopened = await MessageIdRegistry.OpenAsync(path, 1);
            Assert.IsFalse(reopened.TryResolve(1, out _));
            Assert.IsTrue(reopened.TryResolve(2, out var persisted));
            Assert.AreEqual(second, persisted);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task MessageIdRegistry_CanceledRegistrationPreservesMappingsAndEvictionOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "ids.json");
            using var registry = await MessageIdRegistry.OpenAsync(path, 1);
            var first = new OneBotMessageReference(MessageScene.Group, 10, 20);
            var second = new OneBotMessageReference(MessageScene.Group, 10, 21);
            await registry.RegisterAsync(first);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => registry.RegisterAsync(second, canceled.Token));
            using var reopened = await MessageIdRegistry.OpenAsync(path, 1);
            Assert.IsTrue(reopened.TryResolve(1, out var retained));
            Assert.AreEqual(first, retained);
            Assert.IsFalse(reopened.TryGet(second, out _));
            Assert.AreEqual(2, await registry.RegisterAsync(second));
            Assert.IsFalse(registry.TryResolve(1, out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task MessageIdRegistry_ImportsScopedJsonOnceAndIsolatesAccounts()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "ids.json");
            const string legacy = """
                {"Version":1,"NextId":75,"Entries":[
                    {"Id":34,"Scene":1,"PeerId":90,"Sequence":12},
                    {"Id":57,"Scene":1,"PeerId":90,"Sequence":13}
                ]}
                """;
            await File.WriteAllTextAsync(path, legacy);
            var first = new OneBotMessageReference(MessageScene.Group, 90, 12);
            var second = new OneBotMessageReference(MessageScene.Group, 90, 13);
            using (var account = await MessageIdRegistry.OpenAsync(path, 3, accountId: 100))
            {
                Assert.AreEqual(34, await account.RegisterAsync(first));
                Assert.AreEqual(57, await account.RegisterAsync(second));
                Assert.AreEqual(75, await account.RegisterAsync(new(MessageScene.Group, 90, 14)));
                Assert.AreEqual(legacy, await File.ReadAllTextAsync(path));
            }

            using (var otherAccount = await MessageIdRegistry.OpenAsync(path, 3, accountId: 200))
            {
                Assert.IsFalse(otherAccount.TryResolve(34, out _));
                Assert.AreEqual(76, await otherAccount.RegisterAsync(first));
            }

            using var reopened = await MessageIdRegistry.OpenAsync(path, 3, accountId: 100);
            Assert.IsFalse(reopened.TryResolve(34, out _));
            Assert.IsFalse(reopened.TryResolve(76, out _));
            Assert.AreEqual(57, await reopened.RegisterAsync(second));
        }
        finally { directory.Delete(recursive: true); }
    }
}
