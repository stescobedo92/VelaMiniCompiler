using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Ok_WhenCreated_ExposesSuccessfulValue()
    {
        Result<int, string> result = Result.Ok<int, string>(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
        Assert.Throws<InvalidOperationException>(() => _ = result.Error);
    }

    [Fact]
    public void Fail_WhenCreated_ExposesErrorValue()
    {
        Result<int, string> result = Result.Fail<int, string>("invalid input");

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Equal("invalid input", result.Error);
        Assert.Throws<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void Map_WhenSuccessful_ProjectsValueAndPreservesSuccess()
    {
        Result<int, string> result = Result.Ok<int, string>(21);

        Result<int, string> mapped = result.Map(value => value * 2);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(42, mapped.Value);
    }

    [Fact]
    public void Map_WhenFailed_PreservesOriginalError()
    {
        Result<int, string> result = Result.Fail<int, string>("invalid input");

        Result<int, string> mapped = result.Map(value => value * 2);

        Assert.True(mapped.IsFailure);
        Assert.Equal("invalid input", mapped.Error);
    }

    [Fact]
    public void MapError_WhenFailed_ProjectsErrorAndPreservesFailure()
    {
        Result<int, string> result = Result.Fail<int, string>("invalid input");

        Result<int, int> mapped = result.MapError(error => error.Length);

        Assert.True(mapped.IsFailure);
        Assert.Equal("invalid input".Length, mapped.Error);
    }
}
