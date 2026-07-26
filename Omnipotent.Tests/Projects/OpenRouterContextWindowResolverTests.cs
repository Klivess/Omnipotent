using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class OpenRouterContextWindowResolverTests
{
    [Fact]
    public async Task Resolve_UsesSmallestProviderWindowAcrossEveryFallbackRoute()
    {
        var handler = new CatalogHandler(Catalog(
            Model("vendor/large", context: 131_072, providerContext: 100_000, completion: 16_384),
            Model("vendor/small", context: 32_768, providerContext: 32_768, completion: 4_096)));
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>("secret"),
            _ => { },
            http);

        var limits = await resolver.ResolveAsync(
            new[] { "vendor/large", "vendor/small", "vendor/large" });

        Assert.Equal(32_768, limits.ContextWindowTokens);
        Assert.Equal(4_096, limits.MaxCompletionTokens);
        Assert.True(limits.AllRoutesResolved);
        Assert.Equal(2, limits.Routes.Count);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("secret", handler.Authorization?.Parameter);
    }

    [Fact]
    public async Task Resolve_CatalogMissFailsSafeAndMarksRouteSetIncomplete()
    {
        var handler = new CatalogHandler(Catalog(
            Model("vendor/known", context: 128_000, providerContext: 128_000, completion: 8_192)));
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>(null),
            _ => { },
            http);

        var limits = await resolver.ResolveAsync(new[] { "vendor/known", "vendor/new-alias" });

        Assert.Equal(OpenRouterContextWindowResolver.FallbackContextWindowTokens, limits.ContextWindowTokens);
        Assert.Equal(OpenRouterContextWindowResolver.FallbackMaxCompletionTokens, limits.MaxCompletionTokens);
        Assert.False(limits.AllRoutesResolved);
        Assert.False(limits.Routes.Single(x => x.RequestedModel == "vendor/new-alias").FromCatalog);
    }

    [Fact]
    public async Task Resolve_CachesCatalogBetweenHotPathCalls()
    {
        var handler = new CatalogHandler(Catalog(
            Model("vendor/model", context: 64_000, providerContext: 64_000, completion: 8_000)));
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>("secret"),
            _ => { },
            http);

        await resolver.ResolveAsync(new[] { "vendor/model" });
        await resolver.ResolveAsync(new[] { "vendor/model" });

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Resolve_HttpFailureReturnsConservativeLimits()
    {
        var handler = new CatalogHandler("{}", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>("secret"),
            _ => { },
            http);

        var limits = await resolver.ResolveAsync(new[] { "vendor/model" });

        Assert.Equal(8_192, limits.ContextWindowTokens);
        Assert.Equal(2_048, limits.MaxCompletionTokens);
        Assert.False(limits.AllRoutesResolved);
    }

    private static string Model(string id, int context, int providerContext, int completion) =>
        $$"""
          {
            "id": "{{id}}",
            "canonical_slug": "{{id}}",
            "context_length": {{context}},
            "top_provider": {
              "context_length": {{providerContext}},
              "max_completion_tokens": {{completion}}
            }
          }
          """;

    private static string Catalog(params string[] models) =>
        "{\"data\":[" + string.Join(",", models) + "]}";

    private sealed class CatalogHandler : HttpMessageHandler
    {
        private readonly string body;
        private readonly HttpStatusCode status;

        public CatalogHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            this.body = body;
            this.status = status;
        }

        public int Calls { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
