using System;
using System.Collections.Generic;
using System.IO;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// Mergeable log-scale histogram over non-negative integers (µs for latency,
    /// bytes for sizes). One fixed bucket scheme everywhere — 2^(1/4) geometric steps
    /// from 10 units up to ~1.2e9 units (≈20 min in µs) — so any two histograms merge
    /// by adding counts. That makes every rollup and re-bucketing exact; only the
    /// percentile read is approximate (reported value is the bucket's geometric
    /// midpoint, ≤ ±9.1 % from any value inside it).
    ///
    /// Two representations: a dense <c>long[112]</c> while being written (O(1) Add),
    /// and a sparse (index, count) pair once <see cref="Freeze"/>d — closed buckets
    /// typically touch only a handful of buckets, so this keeps retained history small.
    /// </summary>
    public sealed class LogHistogram
    {
        public const int BucketCount = 112;
        public const double MinValue = 10.0;
        private const double StepsPerOctave = 4.0;

        private long[]? _dense;
        private byte[]? _sparseIdx;
        private long[]? _sparseCnt;

        public long Count { get; private set; }
        public long Sum { get; private set; }
        public long Max { get; private set; }

        public bool IsFrozen => _dense == null && _sparseIdx != null;
        public bool IsEmpty => Count == 0;

        public static int IndexOf(long value)
        {
            if (value < MinValue) return 0;
            int i = 1 + (int)Math.Floor(StepsPerOctave * Math.Log2(value / MinValue));
            return i >= BucketCount ? BucketCount - 1 : i;
        }

        public static double BucketLow(int index) =>
            index <= 0 ? 0 : MinValue * Math.Pow(2, (index - 1) / StepsPerOctave);

        public static double BucketHigh(int index) =>
            index <= 0 ? MinValue : MinValue * Math.Pow(2, index / StepsPerOctave);

        /// <summary>Representative (geometric midpoint) value of a bucket.</summary>
        public static double BucketMid(int index) =>
            index <= 0 ? MinValue / 2 : MinValue * Math.Pow(2, (index - 0.5) / StepsPerOctave);

        public void Add(long value)
        {
            if (value < 0) value = 0;
            EnsureDense();
            _dense![IndexOf(value)]++;
            Count++;
            Sum += value;
            if (value > Max) Max = value;
        }

        public void AddToBucket(int index, long count, long sumContribution, long max)
        {
            if (count <= 0) return;
            EnsureDense();
            _dense![Math.Clamp(index, 0, BucketCount - 1)] += count;
            Count += count;
            Sum += sumContribution;
            if (max > Max) Max = max;
        }

        public void Merge(LogHistogram? other)
        {
            if (other == null || other.Count == 0) return;
            EnsureDense();
            if (other._dense != null)
            {
                for (int i = 0; i < BucketCount; i++) _dense![i] += other._dense[i];
            }
            else if (other._sparseIdx != null)
            {
                for (int i = 0; i < other._sparseIdx.Length; i++) _dense![other._sparseIdx[i]] += other._sparseCnt![i];
            }
            Count += other.Count;
            Sum += other.Sum;
            if (other.Max > Max) Max = other.Max;
        }

        /// <summary>
        /// Removes <paramref name="other"/>'s counts (sliding-window totals). Max cannot be
        /// un-merged, so callers that subtract must recompute Max from the window.
        /// </summary>
        public void Subtract(LogHistogram? other)
        {
            if (other == null || other.Count == 0) return;
            EnsureDense();
            foreach (var (idx, cnt) in other.NonEmpty()) _dense![idx] = Math.Max(0, _dense[idx] - cnt);
            Count = Math.Max(0, Count - other.Count);
            Sum = Math.Max(0, Sum - other.Sum);
        }

        public void SetMax(long max) => Max = max;

        /// <summary>Converts to the compact sparse form. Further Adds re-densify.</summary>
        public LogHistogram Freeze()
        {
            if (_dense == null) return this;
            int n = 0;
            for (int i = 0; i < BucketCount; i++) if (_dense[i] != 0) n++;
            var idx = new byte[n];
            var cnt = new long[n];
            int j = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                if (_dense[i] == 0) continue;
                idx[j] = (byte)i;
                cnt[j] = _dense[i];
                j++;
            }
            _sparseIdx = idx;
            _sparseCnt = cnt;
            _dense = null;
            return this;
        }

        public LogHistogram Clone()
        {
            var h = new LogHistogram();
            h.Merge(this);
            return h;
        }

        public IEnumerable<(int Index, long Count)> NonEmpty()
        {
            if (_dense != null)
            {
                for (int i = 0; i < BucketCount; i++) if (_dense[i] != 0) yield return (i, _dense[i]);
            }
            else if (_sparseIdx != null)
            {
                for (int i = 0; i < _sparseIdx.Length; i++) yield return (_sparseIdx[i], _sparseCnt![i]);
            }
        }

        public long CountAt(int index)
        {
            if (_dense != null) return _dense[index];
            if (_sparseIdx == null) return 0;
            for (int i = 0; i < _sparseIdx.Length; i++) if (_sparseIdx[i] == index) return _sparseCnt![i];
            return 0;
        }

        public double Mean => Count == 0 ? 0 : (double)Sum / Count;

        /// <summary>Approximate p-quantile (0..1). Never exceeds the observed max.</summary>
        public double Percentile(double p)
        {
            if (Count == 0) return 0;
            p = Math.Clamp(p, 0, 1);
            long rank = Math.Max(1, (long)Math.Ceiling(p * Count));
            long seen = 0;
            foreach (var (idx, cnt) in NonEmpty())
            {
                seen += cnt;
                if (seen >= rank) return Math.Min(BucketMid(idx), Max);
            }
            return Max;
        }

        /// <summary>Fraction (by count) of values ≤ <paramref name="threshold"/>, bucket-resolution.</summary>
        public double FractionAtOrBelow(double threshold)
        {
            if (Count == 0) return 1;
            long under = 0;
            foreach (var (idx, cnt) in NonEmpty())
            {
                if (BucketHigh(idx) <= threshold) under += cnt;
                else if (BucketLow(idx) < threshold)
                {
                    // Partial bucket: linear share in log space.
                    double lo = Math.Max(BucketLow(idx), 1), hi = BucketHigh(idx);
                    double f = (Math.Log(threshold) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo));
                    under += (long)Math.Round(cnt * Math.Clamp(f, 0, 1));
                }
            }
            return (double)under / Count;
        }

        private void EnsureDense()
        {
            if (_dense != null) return;
            _dense = new long[BucketCount];
            if (_sparseIdx != null)
            {
                for (int i = 0; i < _sparseIdx.Length; i++) _dense[_sparseIdx[i]] = _sparseCnt![i];
                _sparseIdx = null;
                _sparseCnt = null;
            }
        }

        // ── binary form (sparse; used by persistence) ──

        public void Write(BinaryWriter w)
        {
            w.Write7BitEncodedInt64(Count);
            if (Count == 0) return;
            w.Write7BitEncodedInt64(Sum);
            w.Write7BitEncodedInt64(Max);
            int n = 0;
            foreach (var _ in NonEmpty()) n++;
            w.Write((byte)n);
            foreach (var (idx, cnt) in NonEmpty())
            {
                w.Write((byte)idx);
                w.Write7BitEncodedInt64(cnt);
            }
        }

        public static LogHistogram Read(BinaryReader r)
        {
            var h = new LogHistogram();
            long count = r.Read7BitEncodedInt64();
            if (count == 0) return h;
            long sum = r.Read7BitEncodedInt64();
            long max = r.Read7BitEncodedInt64();
            int n = r.ReadByte();
            var idx = new byte[n];
            var cnt = new long[n];
            for (int i = 0; i < n; i++)
            {
                idx[i] = r.ReadByte();
                cnt[i] = r.Read7BitEncodedInt64();
            }
            h._sparseIdx = idx;
            h._sparseCnt = cnt;
            h.Count = count;
            h.Sum = sum;
            h.Max = max;
            return h;
        }

        /// <summary>Rough retained size in bytes, for memory budgeting.</summary>
        public int EstimatedBytes => _dense != null ? BucketCount * 8 + 48 : (_sparseIdx?.Length ?? 0) * 9 + 64;
    }
}
