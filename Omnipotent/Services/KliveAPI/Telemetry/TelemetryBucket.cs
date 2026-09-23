using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// Aggregate of every request of one series (a route, or a pseudo-series such as
    /// the global total) inside one time window. Everything in here is additive, so
    /// buckets merge exactly across time (rollups, re-bucketing) and across series.
    /// Written by the single aggregation thread while open; immutable once frozen.
    /// </summary>
    public sealed class TelemetryBucket
    {
        public const int StatusSlots = 9;
        public static readonly string[] StatusKeys = { "2xx", "304", "3xx", "401", "403", "404", "429", "4xx", "5xx" };

        public long Count;
        public readonly long[] Status = new long[StatusSlots];
        public long CacheHit, CacheMiss, CacheBypass;
        public long EncBrotli, EncGzip, EncNone;
        public long BytesIn, BytesOut, BytesOutRaw;
        public long Disconnects, Exceptions, ViaBatch, Denied;
        public long InFlightSum;
        public int InFlightMax;

        /// <summary>Client-visible latency (µs), excludes DefenceDelay + Teardown.</summary>
        public LogHistogram Latency = new();
        /// <summary>Per-stage duration (µs) — one entry per request per stage, zeros included.</summary>
        public readonly LogHistogram[] Stages = new LogHistogram[TelemetryStages.Count];
        /// <summary>Response size on the wire (bytes).</summary>
        public LogHistogram Size = new();

        public Dictionary<string, long>? Origins;
        public Dictionary<string, long>? Users;
        public Dictionary<string, SpanAgg>? Spans;

        public const int MaxUsers = 32;
        public const int MaxSpanNames = 16;
        public const int MaxOrigins = 12;

        public sealed class SpanAgg
        {
            public long Count;
            public long SumMicros;
            public long MaxMicros;
        }

        public TelemetryBucket()
        {
            for (int i = 0; i < Stages.Length; i++) Stages[i] = new LogHistogram();
        }

        public static int StatusSlot(int status)
        {
            if (status == 304) return 1;
            if (status >= 500) return 8;
            if (status == 401) return 3;
            if (status == 403) return 4;
            if (status == 404) return 5;
            if (status == 429) return 6;
            if (status >= 400) return 7;
            if (status >= 300) return 2;
            return 0; // 1xx/2xx (and 0 which the pipeline maps to 500 before we see it)
        }

        public long ServerErrors => Status[8];
        public long ClientErrors => Status[3] + Status[4] + Status[5] + Status[6] + Status[7];

        public void Fold(RequestTrace t)
        {
            Count++;
            Status[StatusSlot(t.StatusCode)]++;
            switch (t.Cache)
            {
                case TelemetryCacheStatus.Hit: CacheHit++; break;
                case TelemetryCacheStatus.Miss: CacheMiss++; break;
                case TelemetryCacheStatus.Bypass: CacheBypass++; break;
            }
            if (t.ResponseBytes > 0)
            {
                if (t.Encoding == "br") EncBrotli++;
                else if (t.Encoding == "gzip") EncGzip++;
                else EncNone++;
            }
            BytesIn += Math.Max(0, t.RequestBytes);
            BytesOut += Math.Max(0, t.ResponseBytes);
            BytesOutRaw += Math.Max(0, t.ResponseRawBytes > 0 ? t.ResponseRawBytes : t.ResponseBytes);
            if (t.ClientDisconnected) Disconnects++;
            if (t.Exception) Exceptions++;
            if (t.ViaBatch) ViaBatch++;
            if (t.Denied) Denied++;
            InFlightSum += t.InFlightAtStart;
            if (t.InFlightAtStart > InFlightMax) InFlightMax = t.InFlightAtStart;

            Latency.Add(RequestTrace.TicksToMicros(t.LatencyTicks));
            for (int i = 0; i < Stages.Length; i++)
            {
                Stages[i].Add(RequestTrace.TicksToMicros(t.StageTicks[i]));
            }
            Size.Add(Math.Max(0, t.ResponseBytes));

            if (!string.IsNullOrEmpty(t.Origin))
            {
                Origins ??= new Dictionary<string, long>(StringComparer.Ordinal);
                if (Origins.ContainsKey(t.Origin) || Origins.Count < MaxOrigins)
                {
                    Origins[t.Origin] = Origins.GetValueOrDefault(t.Origin) + 1;
                }
            }

            string? user = t.ProfileName ?? t.ProfileId;
            if (!string.IsNullOrEmpty(user)) AddUser(user, 1);

            for (int s = 0; s < t.SpanCount; s++)
            {
                AddSpan(t.SpanName(s), 1, RequestTrace.TicksToMicros(t.SpanTicks(s)), RequestTrace.TicksToMicros(t.SpanTicks(s)));
            }
        }

        private void AddUser(string user, long count)
        {
            Users ??= new Dictionary<string, long>(StringComparer.Ordinal);
            if (Users.TryGetValue(user, out long existing))
            {
                Users[user] = existing + count;
                return;
            }
            if (Users.Count >= MaxUsers)
            {
                // Space-saving: evict the smallest and inherit its count as the error bound.
                var min = Users.MinBy(kv => kv.Value);
                Users.Remove(min.Key);
                Users[user] = min.Value + count;
                return;
            }
            Users[user] = count;
        }

        private void AddSpan(string name, long count, long sumMicros, long maxMicros)
        {
            Spans ??= new Dictionary<string, SpanAgg>(StringComparer.Ordinal);
            if (!Spans.TryGetValue(name, out SpanAgg? agg))
            {
                if (Spans.Count >= MaxSpanNames) return;
                agg = new SpanAgg();
                Spans[name] = agg;
            }
            agg.Count += count;
            agg.SumMicros += sumMicros;
            if (maxMicros > agg.MaxMicros) agg.MaxMicros = maxMicros;
        }

        public void Merge(TelemetryBucket? o)
        {
            if (o == null || o.Count == 0 && o.Latency.Count == 0) return;
            Count += o.Count;
            for (int i = 0; i < StatusSlots; i++) Status[i] += o.Status[i];
            CacheHit += o.CacheHit; CacheMiss += o.CacheMiss; CacheBypass += o.CacheBypass;
            EncBrotli += o.EncBrotli; EncGzip += o.EncGzip; EncNone += o.EncNone;
            BytesIn += o.BytesIn; BytesOut += o.BytesOut; BytesOutRaw += o.BytesOutRaw;
            Disconnects += o.Disconnects; Exceptions += o.Exceptions; ViaBatch += o.ViaBatch; Denied += o.Denied;
            InFlightSum += o.InFlightSum;
            if (o.InFlightMax > InFlightMax) InFlightMax = o.InFlightMax;
            Latency.Merge(o.Latency);
            for (int i = 0; i < Stages.Length; i++) Stages[i].Merge(o.Stages[i]);
            Size.Merge(o.Size);
            if (o.Origins != null)
            {
                Origins ??= new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var kv in o.Origins)
                {
                    if (Origins.ContainsKey(kv.Key) || Origins.Count < MaxOrigins)
                        Origins[kv.Key] = Origins.GetValueOrDefault(kv.Key) + kv.Value;
                }
            }
            if (o.Users != null) foreach (var kv in o.Users) AddUser(kv.Key, kv.Value);
            if (o.Spans != null) foreach (var kv in o.Spans) AddSpan(kv.Key, kv.Value.Count, kv.Value.SumMicros, kv.Value.MaxMicros);
        }

        public TelemetryBucket Freeze()
        {
            Latency.Freeze();
            Size.Freeze();
            foreach (var h in Stages) h.Freeze();
            return this;
        }

        public int EstimatedBytes
        {
            get
            {
                int n = 256 + Latency.EstimatedBytes + Size.EstimatedBytes;
                foreach (var h in Stages) n += h.EstimatedBytes;
                n += (Origins?.Count ?? 0) * 48 + (Users?.Count ?? 0) * 64 + (Spans?.Count ?? 0) * 80;
                return n;
            }
        }

        // ── persistence ──

        private const byte FormatVersion = 1;

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(256);
            using (var w = new BinaryWriter(ms))
            {
                w.Write(FormatVersion);
                w.Write7BitEncodedInt64(Count);
                for (int i = 0; i < StatusSlots; i++) w.Write7BitEncodedInt64(Status[i]);
                w.Write7BitEncodedInt64(CacheHit); w.Write7BitEncodedInt64(CacheMiss); w.Write7BitEncodedInt64(CacheBypass);
                w.Write7BitEncodedInt64(EncBrotli); w.Write7BitEncodedInt64(EncGzip); w.Write7BitEncodedInt64(EncNone);
                w.Write7BitEncodedInt64(BytesIn); w.Write7BitEncodedInt64(BytesOut); w.Write7BitEncodedInt64(BytesOutRaw);
                w.Write7BitEncodedInt64(Disconnects); w.Write7BitEncodedInt64(Exceptions);
                w.Write7BitEncodedInt64(ViaBatch); w.Write7BitEncodedInt64(Denied);
                w.Write7BitEncodedInt64(InFlightSum); w.Write7BitEncodedInt(InFlightMax);
                Latency.Write(w);
                w.Write((byte)Stages.Length);
                foreach (var h in Stages) h.Write(w);
                Size.Write(w);
                WriteDict(w, Origins);
                WriteDict(w, Users);
                w.Write((byte)(Spans?.Count ?? 0));
                if (Spans != null)
                {
                    foreach (var kv in Spans)
                    {
                        w.Write(kv.Key);
                        w.Write7BitEncodedInt64(kv.Value.Count);
                        w.Write7BitEncodedInt64(kv.Value.SumMicros);
                        w.Write7BitEncodedInt64(kv.Value.MaxMicros);
                    }
                }
            }
            return ms.ToArray();
        }

        public static TelemetryBucket Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var b = new TelemetryBucket();
            byte version = r.ReadByte();
            if (version != FormatVersion) throw new InvalidDataException($"Unknown telemetry bucket format {version}.");
            b.Count = r.Read7BitEncodedInt64();
            for (int i = 0; i < StatusSlots; i++) b.Status[i] = r.Read7BitEncodedInt64();
            b.CacheHit = r.Read7BitEncodedInt64(); b.CacheMiss = r.Read7BitEncodedInt64(); b.CacheBypass = r.Read7BitEncodedInt64();
            b.EncBrotli = r.Read7BitEncodedInt64(); b.EncGzip = r.Read7BitEncodedInt64(); b.EncNone = r.Read7BitEncodedInt64();
            b.BytesIn = r.Read7BitEncodedInt64(); b.BytesOut = r.Read7BitEncodedInt64(); b.BytesOutRaw = r.Read7BitEncodedInt64();
            b.Disconnects = r.Read7BitEncodedInt64(); b.Exceptions = r.Read7BitEncodedInt64();
            b.ViaBatch = r.Read7BitEncodedInt64(); b.Denied = r.Read7BitEncodedInt64();
            b.InFlightSum = r.Read7BitEncodedInt64(); b.InFlightMax = r.Read7BitEncodedInt();
            b.Latency = LogHistogram.Read(r);
            int stages = r.ReadByte();
            for (int i = 0; i < stages; i++)
            {
                var h = LogHistogram.Read(r);
                if (i < b.Stages.Length) b.Stages[i] = h;
            }
            b.Size = LogHistogram.Read(r);
            b.Origins = ReadDict(r);
            b.Users = ReadDict(r);
            int spans = r.ReadByte();
            if (spans > 0)
            {
                b.Spans = new Dictionary<string, SpanAgg>(StringComparer.Ordinal);
                for (int i = 0; i < spans; i++)
                {
                    string name = r.ReadString();
                    b.Spans[name] = new SpanAgg
                    {
                        Count = r.Read7BitEncodedInt64(),
                        SumMicros = r.Read7BitEncodedInt64(),
                        MaxMicros = r.Read7BitEncodedInt64(),
                    };
                }
            }
            return b;
        }

        private static void WriteDict(BinaryWriter w, Dictionary<string, long>? d)
        {
            w.Write((byte)Math.Min(255, d?.Count ?? 0));
            if (d == null) return;
            int n = 0;
            foreach (var kv in d)
            {
                if (n++ >= 255) break;
                w.Write(kv.Key);
                w.Write7BitEncodedInt64(kv.Value);
            }
        }

        private static Dictionary<string, long>? ReadDict(BinaryReader r)
        {
            int n = r.ReadByte();
            if (n == 0) return null;
            var d = new Dictionary<string, long>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++) d[r.ReadString()] = r.Read7BitEncodedInt64();
            return d;
        }
    }
}
