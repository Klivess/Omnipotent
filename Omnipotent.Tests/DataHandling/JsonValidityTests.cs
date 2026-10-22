using Omnipotent.Data_Handling;

namespace Omnipotent.Tests.DataHandling;

/// <summary>
/// KliveAPI checks every JSON response with OmniPaths.IsValidJson. It now confirms well-formed text with a
/// forward-only reader instead of building a Newtonsoft document, and only falls back to Newtonsoft's
/// lenient parse for text that reader refuses, so its answers must not change.
/// </summary>
public sealed class JsonValidityTests
{
    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2,3]")]
    [InlineData("  {\"a\":[1,{\"b\":null}],\"c\":\"x\\u0041\"}  \n")]
    [InlineData("[]")]
    [InlineData("{\"a\":1,}")]          // trailing comma
    [InlineData("{/* note */\"a\":1}")] // comment
    [InlineData("{a:1}")]               // unquoted name: Newtonsoft accepted it, so it still passes
    [InlineData("{'a':'b'}")]           // single quotes, likewise
    [InlineData("{\"a\":NaN}")]         // Newtonsoft's NaN literal, likewise
    public void Json_Objects_And_Arrays_Are_Valid(string text) => Assert.True(OmniPaths.IsValidJson(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OK")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":1} {\"b\":2}")]
    [InlineData("[1,2")]
    [InlineData("{\"a\":1]")]
    public void Everything_Else_Is_Not(string text) => Assert.False(OmniPaths.IsValidJson(text));

    [Fact]
    public void A_Large_Response_Is_Accepted()
    {
        string big = "[" + string.Join(",", Enumerable.Range(0, 200_000).Select(i => $"{{\"id\":{i},\"name\":\"item {i}\",\"tags\":[\"a\",\"b\"]}}")) + "]";
        Assert.True(OmniPaths.IsValidJson(big));
        Assert.False(OmniPaths.IsValidJson(big[..^2] + "]"));
    }
}
