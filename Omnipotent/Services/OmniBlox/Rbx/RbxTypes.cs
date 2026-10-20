using System.Text;

namespace Omnipotent.Services.OmniBlox.Rbx
{
    /// <summary>
    /// Roblox property value types, named after what the XML format calls them. The binary format
    /// stores Content, ProtectedString and BinaryString all as its one String type, so a value read
    /// from a binary file arrives as <see cref="String"/> and <see cref="RbxPropertyKinds"/> decides
    /// how it is written back out.
    /// </summary>
    public enum RbxType
    {
        String,
        ProtectedString,
        Content,
        BinaryString,
        Bool,
        Int32,
        Float32,
        Float64,
        Int64,
        UDim,
        UDim2,
        Ray,
        Faces,
        Axes,
        BrickColor,
        Color3,
        Color3uint8,
        Vector2,
        Vector3,
        Vector3int16,
        CFrame,
        OptionalCFrame,
        Enum,
        Ref,
        NumberSequence,
        ColorSequence,
        NumberRange,
        Rect,
        PhysicalProperties,
        SharedString,
        Font,
        UniqueId,
        SecurityCapabilities,
    }

    public readonly record struct RbxVector2(float X, float Y);

    public readonly record struct RbxVector3(float X, float Y, float Z)
    {
        public static readonly RbxVector3 Zero = new(0, 0, 0);
        public static readonly RbxVector3 One = new(1, 1, 1);
        public static RbxVector3 operator +(RbxVector3 a, RbxVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static RbxVector3 operator -(RbxVector3 a, RbxVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static RbxVector3 operator -(RbxVector3 a) => new(-a.X, -a.Y, -a.Z);
        public static RbxVector3 operator *(RbxVector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static RbxVector3 operator *(float s, RbxVector3 a) => a * s;
        public static RbxVector3 operator /(RbxVector3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);
        public float Dot(RbxVector3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public RbxVector3 Cross(RbxVector3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public float Magnitude => MathF.Sqrt(X * X + Y * Y + Z * Z);
        public RbxVector3 Unit => Magnitude > 1e-9f ? this / Magnitude : Zero;
        public RbxVector3 Abs() => new(MathF.Abs(X), MathF.Abs(Y), MathF.Abs(Z));
        public RbxVector3 Min(RbxVector3 b) => new(MathF.Min(X, b.X), MathF.Min(Y, b.Y), MathF.Min(Z, b.Z));
        public RbxVector3 Max(RbxVector3 b) => new(MathF.Max(X, b.X), MathF.Max(Y, b.Y), MathF.Max(Z, b.Z));
        public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###}, {Z:0.###})");
    }

    public readonly record struct RbxVector3int16(short X, short Y, short Z);

    public readonly record struct RbxColor3(float R, float G, float B)
    {
        public RbxColor3uint8 ToUint8() => new(ToByte(R), ToByte(G), ToByte(B));
        private static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
    }

    public readonly record struct RbxColor3uint8(byte R, byte G, byte B)
    {
        public RbxColor3 ToColor3() => new(R / 255f, G / 255f, B / 255f);
        /// <summary>The XML packing: 0xFFRRGGBB.</summary>
        public uint Packed => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        public static RbxColor3uint8 FromPacked(uint argb) => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    }

    public readonly record struct RbxUDim(float Scale, int Offset);

    public readonly record struct RbxUDim2(RbxUDim X, RbxUDim Y);

    public readonly record struct RbxRay(RbxVector3 Origin, RbxVector3 Direction);

    public readonly record struct RbxNumberRange(float Min, float Max);

    public readonly record struct RbxRect(RbxVector2 Min, RbxVector2 Max);

    public readonly record struct RbxNumberKeypoint(float Time, float Value, float Envelope);

    public readonly record struct RbxColorKeypoint(float Time, RbxColor3 Color, float Envelope);

    public sealed record RbxPhysicalProperties(bool Custom, float Density = 0, float Friction = 0, float Elasticity = 0,
        float FrictionWeight = 0, float ElasticityWeight = 0, float? AcousticAbsorption = null);

    public sealed record RbxFont(string Family, ushort Weight, string Style, string CachedFaceId);

    /// <summary>
    /// A position plus a 3×3 rotation matrix stored row-major (R00 R01 R02 / R10 R11 R12 / R20 R21 R22),
    /// exactly as Roblox serializes it. RightVector is column 0, UpVector column 1, LookVector −column 2.
    /// </summary>
    public readonly struct RbxCFrame : IEquatable<RbxCFrame>
    {
        private readonly float r00, r01, r02, r10, r11, r12, r20, r21, r22;

        public RbxCFrame(RbxVector3 position, float r00, float r01, float r02, float r10, float r11, float r12, float r20, float r21, float r22)
        {
            Position = position;
            this.r00 = r00; this.r01 = r01; this.r02 = r02;
            this.r10 = r10; this.r11 = r11; this.r12 = r12;
            this.r20 = r20; this.r21 = r21; this.r22 = r22;
        }

        public RbxCFrame(RbxVector3 position) : this(position, 1, 0, 0, 0, 1, 0, 0, 0, 1) { }

        public static readonly RbxCFrame Identity = new(RbxVector3.Zero);

        public RbxVector3 Position { get; }
        public float[] Rotation => new[] { r00, r01, r02, r10, r11, r12, r20, r21, r22 };
        public RbxVector3 RightVector => new(r00, r10, r20);
        public RbxVector3 UpVector => new(r01, r11, r21);
        public RbxVector3 LookVector => new(-r02, -r12, -r22);
        public bool IsIdentityRotation => r00 == 1 && r11 == 1 && r22 == 1 && r01 == 0 && r02 == 0 && r10 == 0 && r12 == 0 && r20 == 0 && r21 == 0;

        public static RbxCFrame FromRotation(RbxVector3 position, float[] m) => new(position, m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8]);

        /// <summary>Rotation about the world Y axis (radians), the only rotation the builder normally needs.</summary>
        public static RbxCFrame FromYaw(RbxVector3 position, float yawRadians)
        {
            float c = MathF.Cos(yawRadians), s = MathF.Sin(yawRadians);
            // Same matrix as CFrame.Angles(0, yaw, 0).
            return new RbxCFrame(position, c, 0, s, 0, 1, 0, -s, 0, c);
        }

        /// <summary>CFrame.fromEulerAnglesXYZ(rx, ry, rz) — rotation applied as Rx * Ry * Rz.</summary>
        public static RbxCFrame FromEulerXYZ(RbxVector3 position, float rx, float ry, float rz)
        {
            var x = new RbxCFrame(RbxVector3.Zero, 1, 0, 0, 0, MathF.Cos(rx), -MathF.Sin(rx), 0, MathF.Sin(rx), MathF.Cos(rx));
            var y = new RbxCFrame(RbxVector3.Zero, MathF.Cos(ry), 0, MathF.Sin(ry), 0, 1, 0, -MathF.Sin(ry), 0, MathF.Cos(ry));
            var z = new RbxCFrame(RbxVector3.Zero, MathF.Cos(rz), -MathF.Sin(rz), 0, MathF.Sin(rz), MathF.Cos(rz), 0, 0, 0, 1);
            var r = x * y * z;
            return new RbxCFrame(position, r.r00, r.r01, r.r02, r.r10, r.r11, r.r12, r.r20, r.r21, r.r22);
        }

        public RbxVector3 RotateVector(RbxVector3 v) => new(
            r00 * v.X + r01 * v.Y + r02 * v.Z,
            r10 * v.X + r11 * v.Y + r12 * v.Z,
            r20 * v.X + r21 * v.Y + r22 * v.Z);

        public RbxVector3 PointToWorldSpace(RbxVector3 local) => Position + RotateVector(local);

        public RbxVector3 PointToObjectSpace(RbxVector3 world)
        {
            var d = world - Position;
            // Inverse of a rotation matrix is its transpose.
            return new RbxVector3(r00 * d.X + r10 * d.Y + r20 * d.Z, r01 * d.X + r11 * d.Y + r21 * d.Z, r02 * d.X + r12 * d.Y + r22 * d.Z);
        }

        public RbxCFrame Inverse()
        {
            var inv = new RbxCFrame(RbxVector3.Zero, r00, r10, r20, r01, r11, r21, r02, r12, r22);
            return new RbxCFrame(-inv.RotateVector(Position), inv.r00, inv.r01, inv.r02, inv.r10, inv.r11, inv.r12, inv.r20, inv.r21, inv.r22);
        }

        public static RbxCFrame operator *(RbxCFrame a, RbxCFrame b) => new(
            a.PointToWorldSpace(b.Position),
            a.r00 * b.r00 + a.r01 * b.r10 + a.r02 * b.r20, a.r00 * b.r01 + a.r01 * b.r11 + a.r02 * b.r21, a.r00 * b.r02 + a.r01 * b.r12 + a.r02 * b.r22,
            a.r10 * b.r00 + a.r11 * b.r10 + a.r12 * b.r20, a.r10 * b.r01 + a.r11 * b.r11 + a.r12 * b.r21, a.r10 * b.r02 + a.r11 * b.r12 + a.r12 * b.r22,
            a.r20 * b.r00 + a.r21 * b.r10 + a.r22 * b.r20, a.r20 * b.r01 + a.r21 * b.r11 + a.r22 * b.r21, a.r20 * b.r02 + a.r21 * b.r12 + a.r22 * b.r22);

        public RbxCFrame WithPosition(RbxVector3 p) => new(p, r00, r01, r02, r10, r11, r12, r20, r21, r22);

        /// <summary>The rotation's heading about Y in degrees (0 = facing −Z, Roblox's "forward").</summary>
        public float YawDegrees => MathF.Atan2(-LookVector.X, -LookVector.Z) * 180f / MathF.PI;

        public bool Equals(RbxCFrame o) => Position == o.Position && r00 == o.r00 && r01 == o.r01 && r02 == o.r02 && r10 == o.r10 && r11 == o.r11 && r12 == o.r12 && r20 == o.r20 && r21 == o.r21 && r22 == o.r22;
        public override bool Equals(object? obj) => obj is RbxCFrame c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(Position, r00, r11, r22, r01, r10);
        public static bool operator ==(RbxCFrame a, RbxCFrame b) => a.Equals(b);
        public static bool operator !=(RbxCFrame a, RbxCFrame b) => !a.Equals(b);

        /// <summary>
        /// The 24 axis-aligned rotations the binary format abbreviates to one byte. id − 1 = 6·right + up,
        /// where right and up are NormalIds (0 +X, 1 +Y, 2 +Z, 3 −X, 4 −Y, 5 −Z) of the RightVector and
        /// UpVector columns; LookVector follows from their cross product.
        /// </summary>
        public static float[]? BasicRotation(byte id)
        {
            if (id < 2 || id > 36) return null;
            int k = id - 1;
            int right = k / 6, up = k % 6;
            if (right > 5 || right % 3 == up % 3) return null; // parallel axes: not a rotation
            var x = Normal(right);
            var y = Normal(up);
            var z = x.Cross(y);
            return new[] { x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z };
        }

        public static RbxVector3 Normal(int normalId) => normalId switch
        {
            0 => new RbxVector3(1, 0, 0),
            1 => new RbxVector3(0, 1, 0),
            2 => new RbxVector3(0, 0, 1),
            3 => new RbxVector3(-1, 0, 0),
            4 => new RbxVector3(0, -1, 0),
            _ => new RbxVector3(0, 0, -1),
        };

        public override string ToString() => $"CFrame{Position}";
    }

    /// <summary>One property value. <see cref="Value"/> holds the CLR form described per type in <see cref="RbxType"/>.</summary>
    public sealed class RbxValue
    {
        public RbxValue(RbxType type, object? value)
        {
            Type = type;
            Value = value;
        }

        public RbxType Type { get; }

        /// <summary>
        /// String/ProtectedString/Content: <see cref="string"/>; BinaryString/SharedString: <see cref="byte"/>[];
        /// Bool: bool; Int32/BrickColor: int; Float32: float; Float64: double; Int64: long; Enum: uint;
        /// Faces/Axes: byte; Ref: <see cref="RbxInstance"/> or null; OptionalCFrame: <see cref="RbxCFrame"/>?;
        /// UniqueId: byte[16]; SecurityCapabilities: ulong; sequences: lists of keypoints; everything else
        /// its record struct.
        /// </summary>
        public object? Value { get; }

        public static RbxValue Str(string s) => new(RbxType.String, s);
        public static RbxValue Source(string s) => new(RbxType.ProtectedString, s);
        public static RbxValue ContentUrl(string? url) => new(RbxType.Content, url ?? "");
        public static RbxValue Bool(bool b) => new(RbxType.Bool, b);
        public static RbxValue Int(int i) => new(RbxType.Int32, i);
        public static RbxValue Float(float f) => new(RbxType.Float32, f);
        public static RbxValue Double(double d) => new(RbxType.Float64, d);
        public static RbxValue Token(uint e) => new(RbxType.Enum, e);
        public static RbxValue Vec3(RbxVector3 v) => new(RbxType.Vector3, v);
        public static RbxValue Vec2(RbxVector2 v) => new(RbxType.Vector2, v);
        public static RbxValue Frame(RbxCFrame c) => new(RbxType.CFrame, c);
        public static RbxValue Color(RbxColor3 c) => new(RbxType.Color3, c);
        public static RbxValue Color8(RbxColor3uint8 c) => new(RbxType.Color3uint8, c);
        public static RbxValue Reference(RbxInstance? target) => new(RbxType.Ref, target);
        public static RbxValue UDim2(RbxUDim2 v) => new(RbxType.UDim2, v);

        public override string ToString() => $"{Type}:{Value}";
    }

    /// <summary>One Roblox instance. Property names are the SERIALIZED names ("size", "Color3uint8", "shape"…).</summary>
    public sealed class RbxInstance
    {
        public RbxInstance(string className, string? name = null)
        {
            ClassName = className;
            if (name != null) Properties["Name"] = RbxValue.Str(name);
        }

        public string ClassName { get; set; }
        public Dictionary<string, RbxValue> Properties { get; } = new(StringComparer.Ordinal);
        public List<RbxInstance> Children { get; } = new();
        public RbxInstance? Parent { get; private set; }

        /// <summary>Set by readers to the file's own referent; writers mint their own.</summary>
        public string? Referent { get; set; }

        public string Name
        {
            get => Properties.TryGetValue("Name", out var v) && v.Value is string s ? s : ClassName;
            set => Properties["Name"] = RbxValue.Str(value);
        }

        public RbxInstance AddChild(RbxInstance child)
        {
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
            return child;
        }

        public void RemoveFromParent()
        {
            Parent?.Children.Remove(this);
            Parent = null;
        }

        internal void SetParentDirect(RbxInstance? parent) => Parent = parent;

        public IEnumerable<RbxInstance> Descendants()
        {
            var stack = new Stack<RbxInstance>();
            for (int i = Children.Count - 1; i >= 0; i--) stack.Push(Children[i]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                yield return next;
                for (int i = next.Children.Count - 1; i >= 0; i--) stack.Push(next.Children[i]);
            }
        }

        public IEnumerable<RbxInstance> SelfAndDescendants()
        {
            yield return this;
            foreach (var d in Descendants()) yield return d;
        }

        public RbxInstance? FindFirstChild(string name) => Children.FirstOrDefault(c => c.Name == name);

        public RbxInstance? FindFirstChildOfClass(string className) => Children.FirstOrDefault(c => c.ClassName == className);

        public string FullName
        {
            get
            {
                var parts = new List<string>();
                for (var at = this; at != null; at = at.Parent) parts.Add(at.Name);
                parts.Reverse();
                return string.Join(".", parts);
            }
        }

        // ── typed access (serialized-name aware) ──

        public RbxValue? Get(string name) => Properties.TryGetValue(name, out var v) ? v : null;

        public T? GetAs<T>(string name) where T : struct => Properties.TryGetValue(name, out var v) && v.Value is T t ? t : null;

        public string? GetString(string name) => Properties.TryGetValue(name, out var v) ? v.Value as string : null;

        public bool? GetBool(string name) => GetAs<bool>(name);

        public void Set(string name, RbxValue value) => Properties[name] = value;

        /// <summary>BasePart size: binary and XML files call it "size"; scripts call it "Size".</summary>
        public RbxVector3? Size => GetAs<RbxVector3>("size") ?? GetAs<RbxVector3>("Size");

        public RbxCFrame? CFrame => GetAs<RbxCFrame>("CFrame");

        public RbxColor3uint8? Color
        {
            get
            {
                if (GetAs<RbxColor3uint8>("Color3uint8") is { } c8) return c8;
                if (GetAs<RbxColor3>("Color") is { } c) return c.ToUint8();
                if (GetAs<int>("BrickColor") is { } bc) return RbxBrickColors.ToColor(bc);
                return null;
            }
        }

        public float Transparency => GetAs<float>("Transparency") ?? 0f;

        public override string ToString() => $"{ClassName} \"{Name}\"";
    }

    /// <summary>A model or place file: its top-level instances plus the file's metadata.</summary>
    public sealed class RbxDocument
    {
        public List<RbxInstance> Roots { get; } = new();
        public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

        /// <summary>Properties a reader could not decode (type it does not know), as "Class.Property:type".</summary>
        public List<string> UndecodedProperties { get; } = new();

        public IEnumerable<RbxInstance> AllInstances() => Roots.SelectMany(r => r.SelfAndDescendants());

        public RbxInstance AddRoot(RbxInstance instance)
        {
            instance.RemoveFromParent();
            Roots.Add(instance);
            return instance;
        }
    }

    /// <summary>The XML type a String-typed binary property should be written as.</summary>
    public static class RbxPropertyKinds
    {
        private static readonly HashSet<string> ContentNames = new(StringComparer.Ordinal)
        {
            "MeshId", "TextureID", "TextureId", "SoundId", "Image", "Texture", "AnimationId", "LinkedSource",
            "ColorMap", "MetalnessMap", "NormalMap", "RoughnessMap", "ShirtTemplate", "PantsTemplate", "Graphic",
            "SkyboxBk", "SkyboxDn", "SkyboxFt", "SkyboxLf", "SkyboxRt", "SkyboxUp", "SunTextureId", "MoonTextureId",
            "HoverImage", "PressedImage", "SelectionImage", "CursorIcon", "Face", "OverlayTextureId", "BaseTextureId",
            "PhysicsData", "AssetId", "VideoContent", "Video", "ImageContent", "TextureContent", "MeshContent",
            "BackgroundImage", "ScaleImage", "MoveImage", "RotateImage", "ColorMapContent", "NormalMapContent",
        };

        private static readonly HashSet<string> BinaryNames = new(StringComparer.Ordinal)
        {
            "AttributesSerialize", "Tags", "MeshData", "ChildData", "ModelMeshData", "PhysicalConfigData",
            "SmoothGrid", "MaterialColors", "PhysicsGrid", "ClusterGridV3", "LevelOfDetailData", "AssetBlob",
            "InitialSizeSerialized", "SerializedDefaultAttributes", "RawJoinData", "CollisionGroupData",
        };

        private static readonly HashSet<string> ProtectedNames = new(StringComparer.Ordinal) { "Source" };

        public static RbxType ForStringProperty(string propertyName, byte[] raw)
        {
            if (ProtectedNames.Contains(propertyName)) return RbxType.ProtectedString;
            if (ContentNames.Contains(propertyName)) return RbxType.Content;
            if (BinaryNames.Contains(propertyName)) return RbxType.BinaryString;
            if (!IsValidUtf8(raw)) return RbxType.BinaryString;
            return RbxType.String;
        }

        public static bool IsValidUtf8(byte[] raw)
        {
            try
            {
                _ = new UTF8Encoding(false, true).GetString(raw);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }
    }
}
