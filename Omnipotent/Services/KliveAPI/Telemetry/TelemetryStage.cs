namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// The lifecycle stages of one KliveAPI request, in pipeline order. Every tick
    /// between accept and the end of the request's <c>finally</c> is attributed to
    /// exactly one stage (see <see cref="RequestTrace.Enter"/>), so the stages
    /// always sum to the total.
    /// </summary>
    public enum TelemetryStage
    {
        /// <summary>Accept → a pool thread starts running the request. Rises under thread-pool starvation.</summary>
        DispatchQueue = 0,
        /// <summary>Route normalisation, query parsing, header capture, method/permission checks.</summary>
        Prologue = 1,
        /// <summary>Resolving the caller's profile from the Authorization header.</summary>
        Auth = 2,
        /// <summary>OmniDefence pre-dispatch gate evaluation.</summary>
        DefenceGate = 3,
        /// <summary>Deliberate OmniDefence sleeps (tarpit, repeat-auth-failure). Excluded from latency.</summary>
        DefenceDelay = 4,
        /// <summary>Reading the request body off the socket (client upload).</summary>
        RequestBodyRead = 5,
        /// <summary>Response-cache key build + lookup.</summary>
        CacheLookup = 6,
        /// <summary>The route handler's own work, up to the moment it starts emitting a response.</summary>
        Handler = 7,
        /// <summary>JSON validity check / fallback serialisation / UTF-8 encoding of the response.</summary>
        Encode = 8,
        /// <summary>Weak-ETag hashing of the response body.</summary>
        ETag = 9,
        /// <summary>Brotli/gzip compression of the response body.</summary>
        Compress = 10,
        /// <summary>Handing the response bytes to http.sys (server upload).</summary>
        ResponseWrite = 11,
        /// <summary>Post-response bookkeeping; not visible to the client.</summary>
        Teardown = 12,
    }

    public static class TelemetryStages
    {
        public const int Count = 13;

        /// <summary>Stable machine names (JSON keys, Server-Timing metric names).</summary>
        public static readonly string[] Keys =
        {
            "queue", "prologue", "auth", "gate", "delay", "body", "cache",
            "handler", "encode", "etag", "compress", "write", "teardown"
        };

        public static readonly string[] Labels =
        {
            "Dispatch queue", "Prologue", "Auth", "Defence gate", "Defence delay",
            "Request body", "Cache lookup", "Handler", "Encode", "ETag", "Compress",
            "Response write", "Teardown"
        };

        /// <summary>
        /// Stages that count toward client-visible latency. DefenceDelay is a deliberate
        /// punishment, Teardown happens after the response is closed.
        /// </summary>
        public static bool CountsTowardLatency(int stage) =>
            stage != (int)TelemetryStage.DefenceDelay && stage != (int)TelemetryStage.Teardown;
    }
}
