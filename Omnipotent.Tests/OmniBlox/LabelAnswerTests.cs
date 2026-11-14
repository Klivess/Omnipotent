using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The labeller's answers as models really write them. Live (13 Nov 2026): 1,017 placeable models failed with
    /// "The model did not return a JSON label" (181 of 200 sampled) or a JSON parse error: the label call had 900 tokens,
    /// thinking included, while its 27,238 calls averaged 445, so the long tail was cut off.
    /// </summary>
    public sealed class LabelAnswerTests
    {
        private const string Label = "{\"what\":\"a low-poly oak tree\",\"category\":\"nature\",\"style\":\"low-poly\",\"description\":\"A round {stylised} oak.\",\"themes\":[\"forest\"],\"quality\":7}";

        [Theory]
        [InlineData("Here is the label:\n```json\n" + Label + "\n```\nLet me know {if} you need more.")]
        [InlineData(Label + "\n\n{\"note\":\"a second object after the label\"}")]
        [InlineData("I looked at the sheet. " + Label)]
        public void TheFirstCompleteObjectIsTheLabel_WhateverSurroundsIt(string answer)
        {
            var label = AssetLabeler.Parse(answer);
            Assert.Equal("a low-poly oak tree", label.What);
            Assert.Equal("nature", label.Category);
            Assert.Equal(7, label.Quality);
            Assert.Equal("A round {stylised} oak.", label.Description); // braces inside a string are text
        }

        [Fact]
        public void AnAnswerCutOffMidObject_SaysItWasNotComplete()
        {
            var ex = Assert.Throws<FormatException>(() => AssetLabeler.Parse(Label[..60]));
            Assert.Contains("not complete", ex.Message);
            Assert.Contains("did not return", Assert.Throws<FormatException>(() => AssetLabeler.Parse("The model is a tree; it looks")).Message);
        }

        [Fact]
        public async Task ACutOffAnswer_IsNamedAsSuch_AndTheBudgetLeavesRoomToThink()
        {
            var llm = new OneTurnLlm(new LlmTurn { Success = true, Text = "Thinking about the oak's", FinishReason = "length", CompletionTokens = AssetLabeler.LabelMaxTokens });
            var labeler = new AssetLabeler(llm);
            var record = new AssetRecord { AssetId = 5, Name = "Oak" };
            var ex = await Assert.ThrowsAsync<FormatException>(() => labeler.LabelAsync(record, null, Array.Empty<(string, byte[])>(), CancellationToken.None));
            Assert.StartsWith(AssetLabeler.CutOffMessage, ex.Message);
            Assert.True(llm.Options!.MaxTokens >= 4 * 445, $"the label budget is {llm.Options.MaxTokens}");

            llm.Next = new LlmTurn { Success = true, Text = Label, FinishReason = "stop" };
            var result = await labeler.LabelAsync(record, null, Array.Empty<(string, byte[])>(), CancellationToken.None);
            Assert.Equal("nature", result.Label.Category);
        }

        [Fact]
        public void ModelsFailedByTheirLabel_AreQueuedOnceMore_AndOnlyOnce()
        {
            using var dir = new TempDir();
            var library = new AssetLibrary(dir.Path);
            library.Open();
            library.UpsertDiscovered(new[]
            {
                new CreatorStoreAsset { AssetId = 1, Name = "oak", AssetTypeId = 10, Triangles = 900 },
                new CreatorStoreAsset { AssetId = 2, Name = "crate", AssetTypeId = 10, Triangles = 900 },
                new CreatorStoreAsset { AssetId = 3, Name = "lamp", AssetTypeId = 10, Triangles = 900 },
            }, "test", 0, DateTime.UtcNow);
            library.Dispose();

            string file = Path.Combine(dir.Path, "library.db");
            void Sql(string sql)
            {
                using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            // What the server holds: label failures (placeable), a download failure, and a library from before the requeue.
            Sql("UPDATE assets SET status = 'failed', attempts = 3, placeable = 1, status_reason = 'label: The model did not return a JSON label.' WHERE asset_id = 1;" +
                "UPDATE assets SET status = 'failed', attempts = 3, placeable = 1, status_reason = 'label: Additional text encountered after finished reading JSON content: {. Path '', line 3.' WHERE asset_id = 2;" +
                "UPDATE assets SET status = 'failed', attempts = 3, placeable = 0, status_reason = 'Chunk truncated.' WHERE asset_id = 3;" +
                "PRAGMA user_version = 0;");

            library = new AssetLibrary(dir.Path);
            try
            {
                library.Open();
                Assert.Contains("2 model(s) failed by a cut-off label answer are queued to be labelled again", library.OpenNote);
                Assert.Equal(new long[] { 1, 2 }, library.NextForLabel(10).Select(r => r.AssetId).OrderBy(id => id));
                Assert.Equal("failed", library.Get(3)!.Status);
            }
            finally { library.Dispose(); }

            // A label that fails after the requeue stays failed: it ran once.
            Sql("UPDATE assets SET status = 'failed', attempts = 3, status_reason = 'label: still no JSON' WHERE asset_id = 1;");
            library = new AssetLibrary(dir.Path);
            try
            {
                library.Open();
                Assert.Null(library.OpenNote);
                Assert.Equal("failed", library.Get(1)!.Status);
            }
            finally { library.Dispose(); SqliteConnection.ClearAllPools(); }
        }

        private sealed class OneTurnLlm : IOmniBloxLlm
        {
            public OneTurnLlm(LlmTurn next) => Next = next;
            public LlmTurn Next { get; set; }
            public LlmCallOptions? Options { get; private set; }
            public TimeSpan PrefixLifetime => TimeSpan.FromMinutes(5);
            public Task<(bool Ok, string? Reason)> CheckAvailableAsync(CancellationToken ct) => Task.FromResult((true, (string?)null));
            public bool HasSpareCapacity() => true;
            public void StartSession(string sessionId, string systemPrompt) { }
            public void AppendUser(string sessionId, string text, IReadOnlyList<byte[]>? images = null) { }
            public void AppendStableUser(string sessionId, string text, IReadOnlyList<byte[]>? images = null) { }
            public void AppendAssistant(string sessionId, string text) { }
            public void AppendToolResult(string sessionId, string toolCallId, string toolName, string content) { }
            public Task<LlmTurn> QueryAsync(string sessionId, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
            {
                Options = options;
                return Task.FromResult(Next);
            }
            public int DropPictures(string sessionId, int keepNewest) => 0;
            public void EndSession(string sessionId) { }
        }
    }
}
