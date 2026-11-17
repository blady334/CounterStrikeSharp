using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CounterStrikeSharp.API.Tests;

[CollectionDefinition("Callback ownership", DisableParallelization = true)]
public class CallbackOwnershipCollection { }

[Collection("Callback ownership")]
public class CallbackOwnershipTests
{
    public CallbackOwnershipTests()
    {
        // Removing a reference logs through Application but does not call the engine.
        _ = new Application(NullLoggerFactory.Instance, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    [Fact]
    public void TwoSubscriptionsOfTheSameHandlerHaveIndependentNativeReferences()
    {
        Action handler = () => { };
        var first = new BasePlugin.CallbackSubscriber(handler, handler, () => { });
        var second = new BasePlugin.CallbackSubscriber(handler, handler, () => { });
        try
        {
            Assert.NotEqual(first.GetReferenceIdentifier(), second.GetReferenceIdentifier());
        }
        finally
        {
            FunctionReference.Remove(first.GetReferenceIdentifier());
            FunctionReference.Remove(second.GetReferenceIdentifier());
        }
    }

    [Fact]
    public void RemovingAnOwnedSubscriptionDoesNotEvictTheSharedReference()
    {
        Action handler = () => { };
        var shared = FunctionReference.Create(handler);
        var owned = new BasePlugin.CallbackSubscriber(handler, handler, () => { });
        try
        {
            FunctionReference.Remove(owned.GetReferenceIdentifier());
            Assert.Same(shared, FunctionReference.Create(handler));
        }
        finally
        {
            FunctionReference.Remove(owned.GetReferenceIdentifier());
            FunctionReference.Remove(shared.Identifier);
        }
    }

    [Fact]
    public void DisposingASubscriberTwiceOnlyUnhooksOnce()
    {
        Action handler = () => { };
        var calls = 0;
        var subscriber = new BasePlugin.CallbackSubscriber(handler, handler, () => calls++);
        try
        {
            subscriber.Dispose();
            subscriber.Dispose();
            Assert.Equal(1, calls);
        }
        finally
        {
            FunctionReference.Remove(subscriber.GetReferenceIdentifier());
        }
    }
}
