using System;
using System.Collections.Generic;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Canonical source names for indexed documents. Kept as string constants (not an enum)
    /// because they're stored verbatim in SQLite and exposed on the wire to routes / tools —
    /// a stable string is friendlier to filter on than an enum ordinal.
    /// </summary>
    public static class RagSource
    {
        public const string ProjectsEvents = "projects-events";
        public const string ProjectsDigests = "projects-digests";
        public const string AgentConversations = "agent-conversations";
        public const string AgentMemories = "agent-memories";
        public const string Omniscience = "omniscience";
        public const string RepoDocs = "repo-docs";
        public const string Web = "web";

        public static readonly string[] All =
        {
            ProjectsEvents, ProjectsDigests, AgentConversations, AgentMemories, Omniscience, RepoDocs, Web
        };

        public static bool IsKnown(string source) => Array.IndexOf(All, source) >= 0;
    }

    /// <summary>
    /// One indexed document: the natural unit a connector produces (an event, a conversation,
    /// a memory, a fact group, a markdown file, a fetched web page). <see cref="Content"/> is the
    /// full text served by <c>read_knowledge_doc</c>; retrieval matches on the derived chunks.
    /// </summary>
    public sealed class RagDocument
    {
        public string DocId = "";
        public string Source = "";
        public string? Title;
        public string? Uri;
        public string Content = "";
        public string ContentHash = "";
        public long CreatedAtUnixMs;
        public long IndexedAtUnixMs;
        public long? ExpiresAtUnixMs; // web docs only
        public string? MetaJson;

        /// <summary>Chunking hint: markdown docs get heading-aware splitting, everything else paragraph/sentence.</summary>
        public bool IsMarkdown;
        /// <summary>When true the whole document is a single chunk regardless of length (events, memories, facts).</summary>
        public bool SingleChunk;
        /// <summary>
        /// Caller-supplied chunk boundaries (e.g. conversation turn-pairs). When set, these are used
        /// verbatim instead of the size-based splitter, giving stable per-unit chunk ids so an
        /// append-only-grown document only re-embeds its genuinely new chunks. <see cref="Content"/>
        /// still holds the full document for read_knowledge_doc.
        /// </summary>
        public List<string>? PreChunks;
    }

    /// <summary>A chunk of a document: the granularity at which we embed + FTS-index + retrieve.</summary>
    public sealed class RagChunk
    {
        public string ChunkId = "";
        public string DocId = "";
        public int Seq;
        public string Source = "";
        public long CreatedAtUnixMs;
        public string Text = "";
        public string ContentHash = "";
        public int TokenEstimate;
    }

    /// <summary>A retrieval hit, hydrated with enough to cite and render without a second query.</summary>
    public sealed class RagHit
    {
        public string ChunkId = "";
        public string DocId = "";
        public string Source = "";
        public string? Title;
        public string? Uri;
        public string Text = "";
        public long CreatedAtUnixMs;
        public double Score;
        // Per-leg ranks for debugging (−1 = not present in that leg).
        public int VectorRank = -1;
        public int LexicalRank = -1;
    }

    /// <summary>Options for a retrieval query.</summary>
    public sealed class RagSearchOptions
    {
        public int MaxResults = 8;
        /// <summary>Restrict to these sources (null/empty = all).</summary>
        public IReadOnlyCollection<string>? Sources;
        /// <summary>Drop hits from projects-events / projects-digests of this project (avoids duplicating a project's own log leg).</summary>
        public string? ExcludeProjectId;
        /// <summary>Also federate into Omniscience's raw-message semantic index (tool path only).</summary>
        public bool IncludeMessages;
    }

    /// <summary>
    /// A retrieval hit as consumed by prompt-injection callers (KliveAgent, Projects). Deliberately
    /// small and stable so Projects can depend on it without pulling the whole retriever surface.
    /// </summary>
    public sealed record KnowledgeHit(string Source, string Title, string Text, string DocId, long CreatedAtUnixMs, double Score);

    /// <summary>A single web-search result row (SearXNG).</summary>
    public sealed class WebSearchResult
    {
        public string Title = "";
        public string Url = "";
        public string Content = "";
        public string? Engine;
        public double Score;
    }

    /// <summary>Time helpers — everything in the index is stored as unix epoch milliseconds (UTC).</summary>
    public static class RagTime
    {
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Converts a DateTime to unix ms, treating Unspecified kinds as UTC (JSON round-trips can lose the kind).</summary>
        public static long ToUnixMs(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
            return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        }
    }
}
