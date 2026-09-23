using System.Text;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveAgent;

public class KliveAgentAttachmentAndCacheTests
{
    [Fact]
    public async Task Attachments_AreConversationBoundAndTextIsAvailableToModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "kliveagent-attachments-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new KliveAgentAttachments(root);
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("A useful attached note."));
            var attachment = await store.SaveAsync("conversation_a", "notes.md", "text/markdown", stream);

            Assert.Single(store.Resolve("conversation_a", [attachment.Id]));
            Assert.Throws<FileNotFoundException>(() => store.Get("conversation_b", attachment.Id));
            var prepared = await store.PrepareForModelAsync([attachment], CancellationToken.None);
            Assert.Contains("A useful attached note.", prepared.text);
            Assert.Contains(store.PathFor(attachment), prepared.text);
            Assert.Empty(prepared.images);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImageAttachment_BecomesVisionContentWithoutEmbeddingBytesInConversationMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "kliveagent-image-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new KliveAgentAttachments(root);
            byte[] image = [137, 80, 78, 71, 13, 10, 26, 10];
            await using var stream = new MemoryStream(image);
            var attachment = await store.SaveAsync("conversation_a", "image.png", "image/png", stream);
            var prepared = await store.PrepareForModelAsync([attachment], CancellationToken.None);

            Assert.Single(prepared.images);
            Assert.Equal(image, prepared.images[0].data);
            Assert.Equal("image/png", prepared.images[0].mimeType);
            Assert.DoesNotContain(Convert.ToBase64String(image), Newtonsoft.Json.JsonConvert.SerializeObject(attachment));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PromptCache_JournalsProviderMeasurementsAndPreservesMissingMetrics()
    {
        string root = Path.Combine(Path.GetTempPath(), "kliveagent-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new KliveAgentPromptCache(Path.Combine(root, "usage.jsonl"));
            journal.Record("run-a", 1, new global::Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMResponse
            {
                PromptTokens = 1000, CachedPromptTokens = 600, CacheMetricsAvailable = true,
                CompletionTokens = 50, Provider = "OpenRouter", Model = "test-model",
                RequestDurationMs = 1234, QueueDurationMs = 100, ProviderDurationMs = 1134,
                LatencyBreakdownAvailable = true
            });
            journal.Record("run-a", 2, new global::Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMResponse
            {
                PromptTokens = 1200, CachedPromptTokens = 0, CacheMetricsAvailable = false,
                Provider = "OpenRouter", Model = "test-model"
            });

            var snapshot = journal.GetSnapshot("7d");
            Assert.Equal(2, snapshot.Requests);
            Assert.Equal(1, snapshot.MeasuredRequests);
            Assert.Equal(600, snapshot.CachedTokens);
            Assert.Equal(1000, snapshot.PromptTokens);
            Assert.Equal(1, snapshot.LatencyBreakdownRequests);
            Assert.Single(snapshot.Recent);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
