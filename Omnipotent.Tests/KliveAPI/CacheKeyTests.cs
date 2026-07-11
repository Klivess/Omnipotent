using System.Collections.Specialized;
using System.Web;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// Cache keys must be canonical (query-order independent, case-correct) and vary by
/// user, so two requests that would produce the same response share an entry and two
/// that wouldn't never collide.
/// </summary>
public sealed class CacheKeyTests
{
    private static NameValueCollection Q(string query) => HttpUtility.ParseQueryString(query);

    [Fact]
    public void QueryOrder_DoesNotAffectKey()
    {
        string a = ResponseCache.BuildKey("/route", Q("b=2&a=1"), "user1");
        string b = ResponseCache.BuildKey("/route", Q("a=1&b=2"), "user1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentQueryValues_ProduceDifferentKeys()
    {
        string a = ResponseCache.BuildKey("/route", Q("a=1"), "user1");
        string b = ResponseCache.BuildKey("/route", Q("a=2"), "user1");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RouteCasing_IsNormalized()
    {
        string a = ResponseCache.BuildKey("/Route", Q(""), "user1");
        string b = ResponseCache.BuildKey("/route", Q(""), "user1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentUsers_ProduceDifferentKeys()
    {
        string a = ResponseCache.BuildKey("/route", Q("a=1"), "user1");
        string b = ResponseCache.BuildKey("/route", Q("a=1"), "user2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NullUser_MapsToAnon()
    {
        string a = ResponseCache.BuildKey("/route", Q(""), null);
        string b = ResponseCache.BuildKey("/route", Q(""), "anon");
        Assert.Equal(a, b);
    }

    [Fact]
    public void MultiValueParam_IsStable()
    {
        string a = ResponseCache.BuildKey("/route", Q("a=1&a=2"), null);
        string b = ResponseCache.BuildKey("/route", Q("a=1&a=2"), null);
        Assert.Equal(a, b);
        Assert.NotEqual(a, ResponseCache.BuildKey("/route", Q("a=1"), null));
    }

    [Fact]
    public void QueryValueCase_IsPreserved()
    {
        // Query values are case-sensitive (IDs, tokens); only the route is folded.
        string a = ResponseCache.BuildKey("/route", Q("id=ABC"), null);
        string b = ResponseCache.BuildKey("/route", Q("id=abc"), null);
        Assert.NotEqual(a, b);
    }
}
