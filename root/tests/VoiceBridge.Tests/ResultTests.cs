using VoiceBridge.Core.Results;

namespace VoiceBridge.Tests;

public sealed class ResultTests
{
    [Fact]
    public void SuccessStoresValueWithoutError()
    {
        var result = Result<string>.Success("ready");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal("ready", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FailureStoresErrorWithoutValue()
    {
        var error = new VoiceBridgeError("invalid-source", "The source is not available.");

        var result = Result<string>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Same(error, result.Error);
    }
}
