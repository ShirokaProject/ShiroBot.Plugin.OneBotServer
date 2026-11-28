using System.Diagnostics;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.Plugin.OneBotServer.Transports;

internal sealed class OneBotActionExecutor(IOneBotActionHandler actions, TimeSpan actionTimeout, TimeSpan mediaTimeout)
{
    private const int MediaConcurrency = 4;
    private static readonly TimeSpan DefaultActionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultMediaTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim actionSlots = new(16, 16);
    private readonly SemaphoreSlim mediaSlots = new(MediaConcurrency, MediaConcurrency);
    // Long merged forwards can use all but one media slot.
    private readonly SemaphoreSlim forwardSlots = new(MediaConcurrency - 1, MediaConcurrency - 1);
    // Media actions beyond the running slots wait instead of failing; the cap bounds payloads held while queued.
    private const int MediaQueueCapacity = 16;
    private int mediaWaiting;

    internal OneBotActionExecutor(IOneBotActionHandler actions)
        : this(actions, DefaultActionTimeout, DefaultMediaTimeout)
    {
    }

    private static bool IsMediaAction(string action) =>
        action.StartsWith("send_", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith("forward_", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith("upload_", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith("get_forward_msg", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith("get_image", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith("get_file", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith("download_file", StringComparison.OrdinalIgnoreCase) ||
        action.StartsWith(".handle_quick_operation", StringComparison.OrdinalIgnoreCase);

    internal async Task<OneBotResponse<object?>> ExecuteAsync(OneBotActionRequest request, CancellationToken connectionToken, WebSocketMediaFrame? frame = null)
    {
        connectionToken.ThrowIfCancellationRequested();
        var isMedia = IsMediaAction(request.Action);
        var slots = isMedia ? mediaSlots : actionSlots;
        var deadline = isMedia ? mediaTimeout : actionTimeout;
        var forwardSlot = request.Action is "send_group_forward_msg" or "send_private_forward_msg" or "send_forward_msg"
            ? forwardSlots : null;
        if (isMedia)
        {
            var rejection = await AcquireMediaSlotsAsync(forwardSlot, deadline, connectionToken).ConfigureAwait(false);
            if (rejection is not null) return rejection;
        }
        else if (!slots.Wait(0))
        {
            return OneBotResponse<object?>.Failed(1500, "too many actions are still running");
        }

        var actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        using var activity = isMedia ? new Activity("OneBot.MediaAction").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        var started = Stopwatch.GetTimestamp();
        if (activity is not null)
            BotLog.Log($"[OneBot] trace={activity.TraceId} media start action={request.Action}");
        // Offload the call itself so a handler that blocks before returning a Task can still time out.
        frame?.Retain();
        var execution = Task.Run(() =>
        {
            actionCancellation.Token.ThrowIfCancellationRequested();
            OneBotRequestFrame.Current = frame;
            return actions.HandleAsync(request, actionCancellation.Token);
        });
        try
        {
            var response = await execution.WaitAsync(deadline, connectionToken).ConfigureAwait(false);
            if (activity is not null)
                BotLog.Log($"[OneBot] trace={activity.TraceId} media result retcode={response.RetCode} elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            return response;
        }
        catch (TimeoutException) when (!execution.IsCompleted)
        {
            if (activity is not null)
                BotLog.Warning($"[OneBot] trace={activity.TraceId} media deadline elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            return OneBotResponse<object?>.Failed(1500, $"action timed out after {deadline.TotalSeconds:0.###} seconds");
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested && activity is not null)
        {
            BotLog.Warning($"[OneBot] trace={activity.TraceId} media cancelled by transport elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            throw;
        }
        finally
        {
            _ = ReleaseActionSlotAsync(execution, actionCancellation, slots, forwardSlot, frame);
        }
    }

    private async Task<OneBotResponse<object?>?> AcquireMediaSlotsAsync(SemaphoreSlim? forwardSlot, TimeSpan wait,
        CancellationToken connectionToken)
    {
        var forwardHeld = forwardSlot is null || forwardSlot.Wait(0);
        if (forwardHeld && mediaSlots.Wait(0)) return null;
        if (Interlocked.Increment(ref mediaWaiting) > MediaQueueCapacity)
        {
            Interlocked.Decrement(ref mediaWaiting);
            if (forwardSlot is not null && forwardHeld) forwardSlot.Release();
            return Busy(forwardSlot is not null && !forwardHeld);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            timeout.CancelAfter(wait);
            try
            {
                if (!forwardHeld)
                {
                    await forwardSlot!.WaitAsync(timeout.Token).ConfigureAwait(false);
                    forwardHeld = true;
                }
                await mediaSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (!connectionToken.IsCancellationRequested)
            {
                if (forwardSlot is not null && forwardHeld) forwardSlot.Release();
                return Busy(forwardSlot is not null && !forwardHeld);
            }
            catch
            {
                if (forwardSlot is not null && forwardHeld) forwardSlot.Release();
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref mediaWaiting);
        }

        static OneBotResponse<object?> Busy(bool forward) => OneBotResponse<object?>.Failed(1500,
            forward ? "too many merged forwards are still running" : "too many actions are still running");
    }

    private static async Task ReleaseActionSlotAsync(Task execution, CancellationTokenSource actionCancellation,
        SemaphoreSlim slots, SemaphoreSlim? forwardSlots, WebSocketMediaFrame? frame)
    {
        try
        {
            if (!execution.IsCompleted) await actionCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            BotLog.Error($"[OneBot] 取消 Action 失败: {exception}");
        }
        finally
        {
            // Keep the slot occupied until an uncooperative handler actually exits.
            await execution.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            actionCancellation.Dispose();
            slots.Release();
            forwardSlots?.Release();
            frame?.Dispose();
        }
    }
}
