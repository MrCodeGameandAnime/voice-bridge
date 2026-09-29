using VoiceBridge.Core.Domain;

namespace VoiceBridge.Tests;

public sealed class DomainModelTests
{
    [Fact]
    public void MessageCanPreserveUnknownDirectionTimestampAndSender()
    {
        var message = new Message(
            "Takeout/Voice/Calls/conversation.html",
            RawTimestamp: null,
            Timestamp: null,
            Body: "source body",
            Sender: null,
            Direction: null);

        Assert.Null(message.RawTimestamp);
        Assert.Null(message.Timestamp);
        Assert.Null(message.Sender);
        Assert.Null(message.Direction);
    }
}
