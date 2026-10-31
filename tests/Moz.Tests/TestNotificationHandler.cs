using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.Subscriptions;

namespace Moz.OpcUa;

/// <summary>
/// Minimal ISubscriptionNotificationHandler used by tests: forwards data-change
/// values to a task completion source.
/// </summary>
public sealed class TestNotificationHandler : ISubscriptionNotificationHandler
{
    private readonly Action<DataValueChange> _onNotification;

    private TestNotificationHandler(Action<DataValueChange> onNotification) =>
        _onNotification = onNotification;

    public static TestNotificationHandler Once(TaskCompletionSource<double> received)
    {
        var fired = 0;
        return new TestNotificationHandler(n =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0 &&
                n.Value.WrappedValue.TryGetValue(out double v))
            {
                received.TrySetResult(v);
            }
        });
    }

    public ValueTask OnDataChangeNotificationAsync(ISubscription subscription,
        uint sequenceNumber, DateTime publishTime,
        ReadOnlyMemory<DataValueChange> notification,
        PublishState publishStateMask, IReadOnlyList<string> stringTable)
    {
        foreach (var n in notification.Span)
        {
            _onNotification(n);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnEventDataNotificationAsync(ISubscription subscription,
        uint sequenceNumber, DateTime publishTime,
        ReadOnlyMemory<EventNotification> notification,
        PublishState publishStateMask, IReadOnlyList<string> stringTable) =>
        ValueTask.CompletedTask;

    public ValueTask OnKeepAliveNotificationAsync(ISubscription subscription,
        uint sequenceNumber, DateTime publishTime,
        PublishState publishStateMask) =>
        ValueTask.CompletedTask;

    public ValueTask OnSubscriptionStateChangedAsync(ISubscription subscription,
        Opc.Ua.Client.Subscriptions.SubscriptionState state, PublishState publishStateMask,
        CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}