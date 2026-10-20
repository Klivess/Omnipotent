using System.Buffers.Binary;
using System.Text;

namespace Omnipotent.Services.OmniBlox.Rbx
{
    /// <summary>
    /// Reads Roblox's binary model/place format (.rbxm / .rbxl). The file is a header followed by chunks:
    /// META (metadata), SSTR (shared strings), INST (one per class: its instances' referents), PROP (one
    /// per class+property: that property for every instance of the class), PRNT (the parent links) and
    /// END. Numeric arrays are byte-interleaved and big-endian; signed integers are zigzag-encoded;
    /// floats have their sign bit rotated to the lowest bit; referent arrays are delta-encoded.
    ///
    /// A PROP chunk is self-contained, so a property whose type this reader does not know is skipped
    /// and recorded in <see cref="RbxDocument.UndecodedProperties"/> — the rest of the file still loads.
    /// </summary>
    public static class RbxBinaryReader
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("<roblox!");
        private static readonly byte[] Signature = { 0x89, 0xFF, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool LooksBinary(ReadOnlySpan<byte> data) =>
            data.Length >= 14 && data[..8].SequenceEqual(Magic) && data.Slice(8, 6).SequenceEqual(Signature);

        private sealed class ClassChunk
        {
            public required string Name { get; init; }
            public required int[] Referents { get; init; }
        }

        public static RbxDocument Read(byte[] data)
        {
            if (!LooksBinary(data)) throw new FormatException("Not a Roblox binary file (bad magic).");
            if (data.Length < 32) throw new FormatException("Truncated header.");
            int pos = 32; // magic(8) + signature(6) + version(2) + classes(4) + instances(4) + reserved(8)

            var doc = new RbxDocument();
            var classes = new Dictionary<int, ClassChunk>();
            var instances = new Dictionary<int, RbxInstance>();
            var sharedStrings = new List<byte[]>();
            var pendingRefs = new List<(RbxInstance Owner, string Property, int Referent)>();
            var order = new List<RbxInstance>();
            bool sawParents = false;

            while (pos + 16 <= data.Length)
            {
                string chunkName = Encoding.ASCII.GetString(data, pos, 4);
                uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
                uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8));
                pos += 16;

                byte[] body;
                if (compressed == 0)
                {
                    if (pos + uncompressed > data.Length) throw new FormatException($"{chunkName} chunk overruns the file.");
                    body = data.AsSpan(pos, (int)uncompressed).ToArray();
                    pos += (int)uncompressed;
                }
                else
                {
                    if (pos + compressed > data.Length) throw new FormatException($"{chunkName} chunk overruns the file.");
                    body = RbxCompression.Decompress(data.AsSpan(pos, (int)compressed), (int)uncompressed);
                    pos += (int)compressed;
                }

                var r = new ChunkReader(body);
                switch (chunkName)
                {
                    case "META":
                        {
                            int count = r.I32();
                            for (int i = 0; i < count; i++)
                            {
                                string key = r.Utf8();
                                doc.Metadata[key] = r.Utf8();
                            }
                            break;
                        }
                    case "SSTR":
                        {
                            r.U32(); // version
                            int count = r.I32();
                            for (int i = 0; i < count; i++)
                            {
                                r.Skip(16); // MD5 of the value; recomputed on write
                                sharedStrings.Add(r.Bytes());
                            }
                            break;
                        }
                    case "INST":
                        {
                            int classId = r.I32();
                            string className = r.Utf8();
                            byte format = r.U8();
                            int count = r.I32();
                            int[] refs = r.Referents(count);
                            if (format == 1) r.Skip(count); // service markers
                            classes[classId] = new ClassChunk { Name = className, Referents = refs };
                            foreach (int referent in refs)
                            {
                                var inst = new RbxInstance(className) { Referent = "RBX" + referent.ToString("X") };
                                instances[referent] = inst;
                                order.Add(inst);
                            }
                            break;
                        }
                    case "PROP":
                        ReadProperty(r, classes, instances, sharedStrings, pendingRefs, doc);
                        break;
                    case "PRNT":
                        {
                            sawParents = true;
                            r.U8(); // version
                            int count = r.I32();
                            int[] children = r.Referents(count);
                            int[] parents = r.Referents(count);
                            for (int i = 0; i < count; i++)
                            {
                                if (!instances.TryGetValue(children[i], out var child)) continue;
                                if (parents[i] >= 0 && instances.TryGetValue(parents[i], out var parent))
                                    parent.AddChild(child);
                                else
                                    doc.AddRoot(child);
                            }
                            break;
                        }
                    case "END\0":
                        pos = data.Length;
                        break;
                    default:
                        break; // SIGN and anything newer: not needed to read the tree
                }
            }

            if (!sawParents)
                foreach (var inst in order) doc.AddRoot(inst);

            foreach (var (owner, property, referent) in pendingRefs)
                owner.Properties[property] = RbxValue.Reference(referent >= 0 && instances.TryGetValue(referent, out var target) ? target : null);

            return doc;
        }

        private static void ReadProperty(ChunkReader r, Dictionary<int, ClassChunk> classes, Dictionary<int, RbxInstance> instances,
            List<byte[]> sharedStrings, List<(RbxInstance, string, int)> pendingRefs, RbxDocument doc)
        {
            int classId = r.I32();
            string name = r.Utf8();
            byte typeId = r.U8();
            if (!classes.TryGetValue(classId, out var cls)) return;
            int n = cls.Referents.Length;
            var targets = new RbxInstance[n];
            for (int i = 0; i < n; i++) targets[i] = instances[cls.Referents[i]];

            try
            {
                switch (typeId)
                {
                    case 0x01: // String (also Content, ProtectedString, BinaryString)
                        for (int i = 0; i < n; i++)
                        {
                            byte[] raw = r.Bytes();
                            var kind = RbxPropertyKinds.ForStringProperty(name, raw);
                            targets[i].Properties[name] = kind == RbxType.BinaryString
                                ? new RbxValue(kind, raw)
                                : new RbxValue(kind, Encoding.UTF8.GetString(raw));
                        }
                        break;
                    case 0x02:
                        for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Bool(r.U8() != 0);
                        break;
                    case 0x03:
                        {
                            var v = r.Int32Array(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Int(v[i]);
                            break;
                        }
                    case 0x04:
                        {
                            var v = r.FloatArray(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Float(v[i]);
                            break;
                        }
                    case 0x05:
                        for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Double(r.F64Le());
                        break;
                    case 0x06:
                        {
                            var scale = r.FloatArray(n);
                            var offset = r.Int32Array(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.UDim, new RbxUDim(scale[i], offset[i]));
                            break;
                        }
                    case 0x07:
                        {
                            var sx = r.FloatArray(n);
                            var sy = r.FloatArray(n);
                            var ox = r.Int32Array(n);
                            var oy = r.Int32Array(n);
                            for (int i = 0; i < n; i++)
                                targets[i].Properties[name] = RbxValue.UDim2(new RbxUDim2(new RbxUDim(sx[i], ox[i]), new RbxUDim(sy[i], oy[i])));
                            break;
                        }
                    case 0x08:
                        for (int i = 0; i < n; i++)
                        {
                            var o = new RbxVector3(r.F32Le(), r.F32Le(), r.F32Le());
                            var d = new RbxVector3(r.F32Le(), r.F32Le(), r.F32Le());
                            targets[i].Properties[name] = new RbxValue(RbxType.Ray, new RbxRay(o, d));
                        }
                        break;
                    case 0x09:
                        for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.Faces, r.U8());
                        break;
                    case 0x0A:
                        for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.Axes, r.U8());
                        break;
                    case 0x0B:
                        {
                            var v = r.UInt32Array(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.BrickColor, (int)v[i]);
                            break;
                        }
                    case 0x0C:
                        {
                            var cr = r.FloatArray(n);
                            var cg = r.FloatArray(n);
                            var cb = r.FloatArray(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Color(new RbxColor3(cr[i], cg[i], cb[i]));
                            break;
                        }
                    case 0x0D:
                        {
                            var x = r.FloatArray(n);
                            var y = r.FloatArray(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Vec2(new RbxVector2(x[i], y[i]));
                            break;
                        }
                    case 0x0E:
                        {
                            var x = r.FloatArray(n);
                            var y = r.FloatArray(n);
                            var z = r.FloatArray(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Vec3(new RbxVector3(x[i], y[i], z[i]));
                            break;
                        }
                    case 0x10:
                        {
                            var frames = r.CFrameArray(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Frame(frames[i]);
                            break;
                        }
                    case 0x12:
                        {
                            var v = r.UInt32Array(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Token(v[i]);
                            break;
                        }
                    case 0x13:
                        {
                            var refs = r.Referents(n);
                            for (int i = 0; i < n; i++) pendingRefs.Add((targets[i], name, refs[i]));
                            break;
                        }
                    case 0x14:
                        for (int i = 0; i < n; i++)
                            targets[i].Properties[name] = new RbxValue(RbxType.Vector3int16, new RbxVector3int16(r.I16Le(), r.I16Le(), r.I16Le()));
                        break;
                    case 0x15:
                        for (int i = 0; i < n; i++)
                        {
                            int count = r.I32();
                            var keys = new List<RbxNumberKeypoint>(count);
                            for (int k = 0; k < count; k++) keys.Add(new RbxNumberKeypoint(r.F32Le(), r.F32Le(), r.F32Le()));
                            targets[i].Properties[name] = new RbxValue(RbxType.NumberSequence, keys);
                        }
                        break;
                    case 0x16:
                        for (int i = 0; i < n; i++)
                        {
                            int count = r.I32();
                            var keys = new List<RbxColorKeypoint>(count);
                            for (int k = 0; k < count; k++)
                            {
                                float t = r.F32Le();
                                var c = new RbxColor3(r.F32Le(), r.F32Le(), r.F32Le());
                                keys.Add(new RbxColorKeypoint(t, c, r.F32Le()));
                            }
                            targets[i].Properties[name] = new RbxValue(RbxType.ColorSequence, keys);
                        }
                        break;
                    case 0x17:
                        for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.NumberRange, new RbxNumberRange(r.F32Le(), r.F32Le()));
                        break;
                    case 0x18:
                        {
                            var x0 = r.FloatArray(n);
                            var y0 = r.FloatArray(n);
                            var x1 = r.FloatArray(n);
                            var y1 = r.FloatArray(n);
                            for (int i = 0; i < n; i++)
                                targets[i].Properties[name] = new RbxValue(RbxType.Rect, new RbxRect(new RbxVector2(x0[i], y0[i]), new RbxVector2(x1[i], y1[i])));
                            break;
                        }
                    case 0x19:
                        for (int i = 0; i < n; i++)
                        {
                            byte flags = r.U8();
                            if ((flags & 1) == 0)
                            {
                                targets[i].Properties[name] = new RbxValue(RbxType.PhysicalProperties, new RbxPhysicalProperties(false));
                                continue;
                            }
                            float density = r.F32Le(), friction = r.F32Le(), elasticity = r.F32Le(), fw = r.F32Le(), ew = r.F32Le();
                            float? acoustic = (flags & 2) != 0 ? r.F32Le() : null;
                            targets[i].Properties[name] = new RbxValue(RbxType.PhysicalProperties,
                                new RbxPhysicalProperties(true, density, friction, elasticity, fw, ew, acoustic));
                        }
                        break;
                    case 0x1A:
                        {
                            var cr = r.Raw(n);
                            var cg = r.Raw(n);
                            var cb = r.Raw(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = RbxValue.Color8(new RbxColor3uint8(cr[i], cg[i], cb[i]));
                            break;
                        }
                    case 0x1B:
                        {
                            var v = r.Int64Array(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.Int64, v[i]);
                            break;
                        }
                    case 0x1C:
                        {
                            var idx = r.UInt32Array(n);
                            for (int i = 0; i < n; i++)
                            {
                                byte[] value = idx[i] < sharedStrings.Count ? sharedStrings[(int)idx[i]] : Array.Empty<byte>();
                                targets[i].Properties[name] = new RbxValue(RbxType.SharedString, value);
                            }
                            break;
                        }
                    case 0x1E:
                        {
                            r.U8(); // inner type id (CFrame)
                            var frames = r.CFrameArray(n);
                            r.U8(); // inner type id (Bool)
                            for (int i = 0; i < n; i++)
                            {
                                bool has = r.U8() != 0;
                                targets[i].Properties[name] = new RbxValue(RbxType.OptionalCFrame, has ? frames[i] : (RbxCFrame?)null);
                            }
                            break;
                        }
                    case 0x1F:
                        {
                            byte[] raw = r.InterleavedRaw(n, 16);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.UniqueId, raw.AsSpan(i * 16, 16).ToArray());
                            break;
                        }
                    case 0x20:
                        for (int i = 0; i < n; i++)
                        {
                            string family = r.Utf8();
                            ushort weight = (ushort)r.I16Le();
                            byte style = r.U8();
                            string cached = r.Utf8();
                            targets[i].Properties[name] = new RbxValue(RbxType.Font, new RbxFont(family, weight, style == 1 ? "Italic" : "Normal", cached));
                        }
                        break;
                    case 0x21:
                        {
                            var v = r.Int64Array(n);
                            for (int i = 0; i < n; i++) targets[i].Properties[name] = new RbxValue(RbxType.SecurityCapabilities, unchecked((ulong)v[i]));
                            break;
                        }
                    default:
                        doc.UndecodedProperties.Add($"{cls.Name}.{name}:0x{typeId:X2}");
                        break;
                }
            }
            catch (FormatException)
            {
                // A malformed (or newer-than-us) property stays unread; the instances keep the rest.
                doc.UndecodedProperties.Add($"{cls.Name}.{name}:0x{typeId:X2} (malformed)");
            }
        }

        /// <summary>Little reader over one decompressed chunk body.</summary>
        private sealed class ChunkReader
        {
            private readonly byte[] buf;
            private int pos;

            public ChunkReader(byte[] buf) => this.buf = buf;

            private void Need(int count)
            {
                if (count < 0 || pos + count > buf.Length) throw new FormatException("Chunk truncated.");
            }

            public byte U8() { Need(1); return buf[pos++]; }
            public void Skip(int count) { Need(count); pos += count; }
            public uint U32() { Need(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos)); pos += 4; return v; }
            public int I32() { Need(4); int v = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos)); pos += 4; return v; }
            public short I16Le() { Need(2); short v = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(pos)); pos += 2; return v; }
            public float F32Le() { Need(4); float v = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(pos)); pos += 4; return v; }
            public double F64Le() { Need(8); double v = BinaryPrimitives.ReadDoubleLittleEndian(buf.AsSpan(pos)); pos += 8; return v; }

            public byte[] Bytes()
            {
                int len = I32();
                Need(len);
                var v = buf.AsSpan(pos, len).ToArray();
                pos += len;
                return v;
            }

            public string Utf8() => Encoding.UTF8.GetString(Bytes());

            public byte[] Raw(int count)
            {
                Need(count);
                var v = buf.AsSpan(pos, count).ToArray();
                pos += count;
                return v;
            }

            /// <summary>Un-interleave <paramref name="count"/> values of <paramref name="width"/> bytes each.</summary>
            public byte[] InterleavedRaw(int count, int width)
            {
                Need(count * width);
                var output = new byte[count * width];
                for (int i = 0; i < count; i++)
                    for (int b = 0; b < width; b++)
                        output[i * width + b] = buf[pos + b * count + i];
                pos += count * width;
                return output;
            }

            public uint[] UInt32Array(int count)
            {
                var raw = InterleavedRaw(count, 4);
                var v = new uint[count];
                for (int i = 0; i < count; i++) v[i] = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));
                return v;
            }

            public int[] Int32Array(int count)
            {
                var u = UInt32Array(count);
                var v = new int[count];
                for (int i = 0; i < count; i++) v[i] = (int)(u[i] >> 1) ^ -(int)(u[i] & 1);
                return v;
            }

            public long[] Int64Array(int count)
            {
                var raw = InterleavedRaw(count, 8);
                var v = new long[count];
                for (int i = 0; i < count; i++)
                {
                    ulong u = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(i * 8));
                    v[i] = (long)(u >> 1) ^ -(long)(u & 1);
                }
                return v;
            }

            public float[] FloatArray(int count)
            {
                var u = UInt32Array(count);
                var v = new float[count];
                for (int i = 0; i < count; i++) v[i] = BitConverter.UInt32BitsToSingle((u[i] >> 1) | (u[i] << 31));
                return v;
            }

            public int[] Referents(int count)
            {
                var v = Int32Array(count);
                for (int i = 1; i < count; i++) v[i] += v[i - 1];
                return v;
            }

            public RbxCFrame[] CFrameArray(int count)
            {
                var rotations = new float[count][];
                for (int i = 0; i < count; i++)
                {
                    byte id = U8();
                    if (id == 0)
                    {
                        var m = new float[9];
                        for (int k = 0; k < 9; k++) m[k] = F32Le();
                        rotations[i] = m;
                    }
                    else
                    {
                        rotations[i] = RbxCFrame.BasicRotation(id) ?? throw new FormatException($"Unknown CFrame rotation id 0x{id:X2}.");
                    }
                }
                var x = FloatArray(count);
                var y = FloatArray(count);
                var z = FloatArray(count);
                var frames = new RbxCFrame[count];
                for (int i = 0; i < count; i++) frames[i] = RbxCFrame.FromRotation(new RbxVector3(x[i], y[i], z[i]), rotations[i]);
                return frames;
            }
        }
    }
}
