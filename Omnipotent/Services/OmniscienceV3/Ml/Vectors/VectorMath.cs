using System.Numerics.Tensors;

namespace Omnipotent.Services.OmniscienceV3.Ml
{
    /// <summary>SIMD vector helpers (System.Numerics.Tensors) shared by the encoder, the vector store and the heads.</summary>
    public static class VectorMath
    {
        /// <summary>A unit-length copy (a zero vector stays zero: there is nothing to point at).</summary>
        public static float[] Normalized(ReadOnlySpan<float> v)
        {
            var copy = v.ToArray();
            NormalizeInPlace(copy);
            return copy;
        }

        public static float[] Normalized(float[]? v) => v == null ? Array.Empty<float>() : Normalized((ReadOnlySpan<float>)v);

        public static void NormalizeInPlace(Span<float> v)
        {
            float norm = TensorPrimitives.Norm(v);
            if (norm > 0 && float.IsFinite(norm)) TensorPrimitives.Divide(v, norm, v);
        }

        public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.Dot(a, b);

        public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            if (a.Length != b.Length) throw new ArgumentException("Vectors differ in length.");
            float na = TensorPrimitives.Norm(a), nb = TensorPrimitives.Norm(b);
            return na == 0 || nb == 0 ? 0 : TensorPrimitives.Dot(a, b) / (na * nb);
        }

        /// <summary>
        /// Symmetric per-vector int8 quantisation (vectors.db quant "i8"): q = round(x / scale) with scale = max|x| / 127.
        /// For unit vectors the dot-product error is far below the ranking noise of the encoder itself.
        /// </summary>
        public static (sbyte[] Values, float Scale) QuantizeInt8(ReadOnlySpan<float> v)
        {
            if (v.Length == 0) return (Array.Empty<sbyte>(), 1f);
            // MaxMagnitude returns the signed element with the largest magnitude, not its absolute value.
            float max = MathF.Abs(TensorPrimitives.MaxMagnitude(v));
            if (max <= 0 || !float.IsFinite(max)) return (new sbyte[v.Length], 1f);
            float scale = max / 127f;
            var q = new sbyte[v.Length];
            for (int i = 0; i < v.Length; i++) q[i] = (sbyte)Math.Clamp((int)MathF.Round(v[i] / scale), -127, 127);
            return (q, scale);
        }

        public static float[] DequantizeInt8(ReadOnlySpan<sbyte> q, float scale)
        {
            var v = new float[q.Length];
            for (int i = 0; i < q.Length; i++) v[i] = q[i] * scale;
            return v;
        }

        /// <summary>Dot product of a float query with an int8 vector, scaled back: the vector store's scoring kernel.</summary>
        public static float DotInt8(ReadOnlySpan<float> query, ReadOnlySpan<sbyte> q, float scale, Span<float> scratch)
        {
            for (int i = 0; i < q.Length; i++) scratch[i] = q[i];
            return TensorPrimitives.Dot(query, scratch[..q.Length]) * scale;
        }

        public static byte[] ToBytes(ReadOnlySpan<sbyte> q) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(q).ToArray();

        public static sbyte[] SBytes(byte[] raw) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(raw).ToArray();

        public static byte[] ToBytes(ReadOnlySpan<float> v) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(v).ToArray();

        public static float[] Floats(byte[] raw) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw).ToArray();

        /// <summary>Mean of unit vectors, re-normalised (a centroid on the sphere).</summary>
        public static float[] Centroid(IEnumerable<float[]> vectors, int dims)
        {
            var acc = new float[dims];
            int n = 0;
            foreach (var v in vectors)
            {
                TensorPrimitives.Add(acc, v, acc);
                n++;
            }
            if (n > 0) NormalizeInPlace(acc);
            return acc;
        }
    }
}
