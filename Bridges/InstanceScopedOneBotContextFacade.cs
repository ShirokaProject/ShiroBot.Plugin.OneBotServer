using System.Reflection;
using System.Runtime.ExceptionServices;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.OneBotServer.Bridges;

/// <summary>Scopes every facade call to one adapter. Async adapter calls capture the selected instance before the scope is restored.</summary>
public class InstanceScopedOneBotContextFacade : DispatchProxy
{
    private IOneBotContextFacade _inner = null!;
    private IBotContext _context = null!;
    private string _instanceId = string.Empty;
    private string _storageDirectory = string.Empty;
    private string _cacheDirectory = string.Empty;

    public static IOneBotContextFacade Bind(IOneBotContextFacade inner, IBotContext context, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var proxy = Create<IOneBotContextFacade, InstanceScopedOneBotContextFacade>();
        var binding = (InstanceScopedOneBotContextFacade)(object)proxy;
        binding._inner = inner;
        binding._context = context;
        binding._instanceId = instanceId;
        binding._storageDirectory = OneBotInstanceBinding.GetInstanceStorageDirectory(context.PluginDirectory, instanceId);
        binding._cacheDirectory = Path.Combine(binding._storageDirectory, "cache");
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) throw new MissingMethodException("OneBot facade proxy received no target method.");
        if (targetMethod.Name == "get_StorageDirectory") return _storageDirectory;
        if (targetMethod.Name == nameof(IOneBotContextFacade.CleanCacheAsync))
        {
            if (Directory.Exists(_cacheDirectory)) Directory.Delete(_cacheDirectory, recursive: true);
            Directory.CreateDirectory(_cacheDirectory);
            return Task.CompletedTask;
        }

        var scope = _context.UseInstance(_instanceId);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        finally
        {
            // The target starts async work while selected; its awaits capture this ExecutionContext.
            scope.Dispose();
        }
    }
}
