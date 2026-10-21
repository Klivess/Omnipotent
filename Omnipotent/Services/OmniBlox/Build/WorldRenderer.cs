using System.Collections.Concurrent;
using System.Numerics;
using Omnipotent.Services.OmniBlox.Rbx;
using SkiaSharp;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>A decoded picture of a library model (Roblox's own render of it, transparent background).</summary>
    public sealed class SpriteImage
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>RGBA, row by row, unpremultiplied.</summary>
        public required byte[] Pixels { get; init; }
        /// <summary>The tight box round the opaque pixels (x0, y0, x1, y1), so the sprite stands exactly on the ground.</summary>
        public required (int X0, int Y0, int X1, int Y1) Opaque { get; init; }
        public required Vector3 Average { get; init; }

        public static SpriteImage? Decode(byte[] png)
        {
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap == null) return null;
            int w = bitmap.Width, h = bitmap.Height;
            var pixels = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bitmap.GetPixel(x, y); // unpremultiplied
                    int i = (y * w + x) * 4;
                    pixels[i] = c.Red; pixels[i + 1] = c.Green; pixels[i + 2] = c.Blue; pixels[i + 3] = c.Alpha;
                }
            }
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            double r = 0, g = 0, b = 0, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (pixels[i + 3] < 40) continue;
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; n++;
                }
            if (x1 < 0) return null; // nothing visible (a thumbnail that failed to render)
            return new SpriteImage
            {
                Width = w, Height = h, Pixels = pixels, Opaque = (x0, y0, x1, y1),
                Average = n > 0 ? new Vector3((float)(r / n), (float)(g / n), (float)(b / n)) / 255f : new Vector3(0.6f),
            };
        }

        public Vector4 Sample(float u, float v)
        {
            float fx = Math.Clamp(u, 0, 1) * (Width - 1), fy = Math.Clamp(v, 0, 1) * (Height - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(Width - 1, x0 + 1), y1 = Math.Min(Height - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            Vector4 P(int x, int y)
            {
                int i = (y * Width + x) * 4;
                float a = Pixels[i + 3] / 255f;
                return new Vector4(Pixels[i] / 255f * a, Pixels[i + 1] / 255f * a, Pixels[i + 2] / 255f * a, a);
            }
            var top = Vector4.Lerp(P(x0, y0), P(x1, y0), tx);
            var bottom = Vector4.Lerp(P(x0, y1), P(x1, y1), tx);
            var c = Vector4.Lerp(top, bottom, ty);
            return c.W > 1e-4f ? new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W) : Vector4.Zero;
        }
    }

    /// <summary>Everything a picture of the world is made of.</summary>
    public sealed class RenderWorld
    {
        public required IReadOnlyList<WorldPart> Parts { get; init; }
        public required SceneDocument Scene { get; init; }
        public TerrainMap? Terrain { get; init; }
        /// <summary>Roblox's render of a library model (null when there is none: the model's parts are drawn instead).</summary>
        public Func<long, SpriteImage?> Sprite { get; init; } = _ => null;
    }

    public sealed class RenderRequest
    {
        /// <summary>overview, map, iso, aerial, spawn, zone:&lt;id&gt;, an object id, or a camera "x,y,z>x,y,z".</summary>
        public string View { get; init; } = "iso";
        public int Width { get; init; } = 1280;
        public int Height { get; init; } = 760;
        /// <summary>Object ids drawn on the picture.</summary>
        public bool Labels { get; init; }
        /// <summary>The caption box in the corner (never on marketing art).</summary>
        public bool Caption { get; init; } = true;
        /// <summary>Marketing art: stronger colour, a vignette, no overlays.</summary>
        public bool Marketing { get; init; }
        /// <summary>Supersampling (2 = 4 samples a pixel).</summary>
        public int Supersample { get; init; } = 2;
        /// <summary>A camera to use instead of the named view.</summary>
        public (Vector3 Eye, Vector3 Target, float Fov)? Camera { get; init; }
    }

    /// <summary>
    /// The studio's eyes. A software renderer for a compiled world: generated terrain with smooth light, biome
    /// colours and water; parts with their real shapes, materials and sun shadows (a shadow map); every library
    /// model drawn as Roblox's own picture of it, standing at its real size and position with a soft shadow; the
    /// sky and grading of the game's lighting preset; and, on the map views, the layout plan, a coordinate grid,
    /// labels and the spawn's facing. The first studio drew every mesh as a box on a flat colour, so its agents
    /// never saw what they had built and its thumbnails looked like grey blocks.
    /// </summary>
    public static class WorldRenderer
    {
        private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.55f, 0.78f, 0.30f));

        private static readonly Dictionary<string, Vector3> TerrainColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Grass"] = C(98, 171, 72), ["LeafyGrass"] = C(84, 150, 60), ["Sand"] = C(232, 212, 160), ["Rock"] = C(128, 124, 118),
            ["Snow"] = C(238, 244, 250), ["Ground"] = C(146, 112, 82), ["Mud"] = C(104, 82, 60), ["Slate"] = C(100, 104, 112),
            ["Ice"] = C(182, 222, 242), ["Glacier"] = C(160, 205, 232), ["Sandstone"] = C(214, 164, 112), ["Basalt"] = C(72, 72, 78),
            ["CrackedLava"] = C(214, 86, 40), ["Asphalt"] = C(72, 74, 80), ["Cobblestone"] = C(134, 128, 120), ["Brick"] = C(164, 86, 62),
            ["Pavement"] = C(154, 154, 154), ["Limestone"] = C(212, 202, 172), ["Salt"] = C(232, 232, 228), ["WoodPlanks"] = C(164, 120, 72),
            ["Concrete"] = C(154, 154, 154), ["Water"] = C(60, 150, 220),
        };

        private static Vector3 C(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);
        private static Vector3 V(RbxVector3 v) => new(v.X, v.Y, v.Z);
        private static Vector3 V(RbxColor3uint8 c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        // ─────────────────────────────── camera ───────────────────────────────

        private sealed class Camera
        {
            public Vector3 Eye, Forward, Right, Up;
            public float Focal, Ortho, Cx, Cy;
            public bool Orthographic;
            /// <summary>Haze starts a little short of what the camera looks at: the subject stays crisp, the distance fades.</summary>
            public float FogStart;
            public const float Near = 0.25f;

            public Camera(Vector3 eye, Vector3 target, float fov, float orthoScale, int width, int height, Vector3? up = null)
            {
                Eye = eye;
                FogStart = Vector3.Distance(eye, target) * 0.8f;
                Forward = Vector3.Normalize(target - eye);
                var worldUp = up ?? Vector3.UnitY;
                Right = Vector3.Cross(Forward, worldUp);
                if (Right.LengthSquared() < 1e-6f) Right = Vector3.Cross(Forward, new Vector3(0, 0, -1));
                Right = Vector3.Normalize(Right);
                Up = Vector3.Cross(Right, Forward);
                Cx = width / 2f;
                Cy = height / 2f;
                Orthographic = fov <= 0;
                Ortho = orthoScale;
                Focal = Orthographic ? 1 : height / 2f / MathF.Tan(fov * MathF.PI / 360f);
            }

            public Vector3 ToCamera(Vector3 world)
            {
                var d = world - Eye;
                return new Vector3(Vector3.Dot(d, Right), Vector3.Dot(d, Up), Vector3.Dot(d, Forward));
            }

            public Vector2 Screen(Vector3 c) => Orthographic ? new Vector2(Cx + c.X * Ortho, Cy - c.Y * Ortho) : new Vector2(Cx + Focal * c.X / c.Z, Cy - Focal * c.Y / c.Z);

            public bool Visible(Vector3 c) => Orthographic || c.Z > Near;
        }

        // ─────────────────────────────── geometry ───────────────────────────────

        private struct Vertex
        {
            public Vector3 World;
            public Vector3 Normal;
            public Vector3 Color;
            public Vector2 Uv;
        }

        private sealed class Mesh
        {
            public readonly List<Vertex> Vertices = new();
            public readonly List<int> Indices = new();
            public float Alpha = 1;
            public bool Emissive;
            public bool CastsShadow = true;
            public SpriteImage? Texture;
            /// <summary>Pushes this mesh back a little in depth: the terrain, so a part flush with the ground wins instead of z-fighting it.</summary>
            public float DepthBias;

            public void Tri(Vertex a, Vertex b, Vertex c)
            {
                int i = Vertices.Count;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
                Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
            }

            public void Quad(Vertex a, Vertex b, Vertex c, Vertex d)
            {
                Tri(a, b, c);
                Tri(a, c, d);
            }
        }

        private static Vertex Vx(Vector3 p, Vector3 n, Vector3 color) => new() { World = p, Normal = n, Color = color };

        private static void AddBox(Mesh mesh, RbxCFrame cf, RbxVector3 size, Vector3 color)
        {
            var c = V(cf.Position);
            var rx = V(cf.RightVector) * (size.X / 2);
            var ry = V(cf.UpVector) * (size.Y / 2);
            var rz = -V(cf.LookVector) * (size.Z / 2);
            Vector3 P(int sx, int sy, int sz) => c + rx * sx + ry * sy + rz * sz;
            void Face(Vector3 n, Vector3 a, Vector3 b, Vector3 d, Vector3 e) => mesh.Quad(Vx(a, n, color), Vx(b, n, color), Vx(d, n, color), Vx(e, n, color));
            var nx = Vector3.Normalize(rx); var ny = Vector3.Normalize(ry); var nz = Vector3.Normalize(rz);
            Face(nx, P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1));
            Face(-nx, P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1));
            Face(ny, P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1));
            Face(-ny, P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1));
            Face(nz, P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1));
            Face(-nz, P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1));
        }

        /// <summary>A WedgePart: full bottom and back (+Z) faces, the slope rising from the front (−Z) bottom edge to the back top edge.</summary>
        private static void AddWedge(Mesh mesh, RbxCFrame cf, RbxVector3 size, Vector3 color)
        {
            var c = V(cf.Position);
            var rx = V(cf.RightVector) * (size.X / 2);
            var ry = V(cf.UpVector) * (size.Y / 2);
            var rz = -V(cf.LookVector) * (size.Z / 2);
            Vector3 P(int sx, int sy, int sz) => c + rx * sx + ry * sy + rz * sz;
            var nx = Vector3.Normalize(rx); var ny = Vector3.Normalize(ry); var nz = Vector3.Normalize(rz);
            mesh.Quad(Vx(P(-1, -1, 1), -ny, color), Vx(P(-1, -1, -1), -ny, color), Vx(P(1, -1, -1), -ny, color), Vx(P(1, -1, 1), -ny, color));
            mesh.Quad(Vx(P(1, -1, 1), nz, color), Vx(P(1, 1, 1), nz, color), Vx(P(-1, 1, 1), nz, color), Vx(P(-1, -1, 1), nz, color));
            var slope = Vector3.Normalize(Vector3.Cross(P(1, 1, 1) - P(1, -1, -1), P(-1, -1, -1) - P(1, -1, -1)));
            if (Vector3.Dot(slope, ny) < 0) slope = -slope;
            mesh.Quad(Vx(P(-1, -1, -1), slope, color), Vx(P(-1, 1, 1), slope, color), Vx(P(1, 1, 1), slope, color), Vx(P(1, -1, -1), slope, color));
            mesh.Tri(Vx(P(1, -1, -1), nx, color), Vx(P(1, 1, 1), nx, color), Vx(P(1, -1, 1), nx, color));
            mesh.Tri(Vx(P(-1, -1, 1), -nx, color), Vx(P(-1, 1, 1), -nx, color), Vx(P(-1, -1, -1), -nx, color));
        }

        /// <summary>A Roblox cylinder: its axis runs along the part's X.</summary>
        private static void AddCylinder(Mesh mesh, RbxCFrame cf, RbxVector3 size, Vector3 color, int segments = 18)
        {
            var c = V(cf.Position);
            var ax = V(cf.RightVector);
            var uy = V(cf.UpVector);
            var uz = -V(cf.LookVector);
            float half = size.X / 2, r = MathF.Min(size.Y, size.Z) / 2;
            var top = c + ax * half;
            var bottom = c - ax * half;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                var d0 = uy * MathF.Cos(a0) + uz * MathF.Sin(a0);
                var d1 = uy * MathF.Cos(a1) + uz * MathF.Sin(a1);
                mesh.Quad(Vx(bottom + d0 * r, d0, color), Vx(top + d0 * r, d0, color), Vx(top + d1 * r, d1, color), Vx(bottom + d1 * r, d1, color));
                mesh.Tri(Vx(top, ax, color), Vx(top + d1 * r, ax, color), Vx(top + d0 * r, ax, color));
                mesh.Tri(Vx(bottom, -ax, color), Vx(bottom + d0 * r, -ax, color), Vx(bottom + d1 * r, -ax, color));
            }
        }

        private static void AddBall(Mesh mesh, RbxCFrame cf, RbxVector3 size, Vector3 color, int rings = 9, int segments = 16)
        {
            var c = V(cf.Position);
            float r = MathF.Min(size.X, MathF.Min(size.Y, size.Z)) / 2;
            Vector3 P(int ring, int seg)
            {
                float theta = ring * MathF.PI / rings, phi = seg * MathF.Tau / segments;
                return new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
            }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segments; j++)
                {
                    var a = P(i, j); var b = P(i + 1, j); var d = P(i + 1, j + 1); var e = P(i, j + 1);
                    mesh.Quad(Vx(c + a * r, a, color), Vx(c + b * r, b, color), Vx(c + d * r, d, color), Vx(c + e * r, e, color));
                }
        }

        private static Vector3 MaterialTint(string material, Vector3 color) => material switch
        {
            "Grass" or "LeafyGrass" => color * 0.96f,
            "Wood" or "WoodPlanks" => color * 0.93f,
            "Slate" or "Granite" or "Cobblestone" or "Rock" => color * 0.94f,
            "Metal" or "DiamondPlate" or "Foil" => Vector3.Lerp(color, new Vector3(0.85f), 0.12f),
            _ => color,
        };

        // ─────────────────────────────── the frame ───────────────────────────────

        private sealed class Frame
        {
            public readonly int W, H;
            public readonly Vector3[] Color;
            public readonly float[] Depth;

            public Frame(int w, int h)
            {
                W = w; H = h;
                Color = new Vector3[w * h];
                Depth = new float[w * h];
                Array.Fill(Depth, float.MaxValue);
            }
        }

        private sealed class ShadowMap
        {
            public readonly int Size;
            public readonly float[] Depth;
            public Vector3 Origin, Right, Up, Forward;
            public float Scale;

            public ShadowMap(int size)
            {
                Size = size;
                Depth = new float[size * size];
                Array.Fill(Depth, float.MaxValue);
            }

            public (float X, float Y, float Z) Project(Vector3 world)
            {
                var d = world - Origin;
                return (Size / 2f + Vector3.Dot(d, Right) * Scale, Size / 2f - Vector3.Dot(d, Up) * Scale, Vector3.Dot(d, Forward));
            }

            /// <summary>0 (in shadow) to 1 (lit), softened over a 2×2 neighbourhood.</summary>
            public float Light(Vector3 world, Vector3 normal)
            {
                var p = world + normal * 0.25f;
                var (x, y, z) = Project(p);
                float bias = 0.6f;
                float lit = 0;
                for (int dy = 0; dy <= 1; dy++)
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        int sx = (int)(x - 0.5f) + dx, sy = (int)(y - 0.5f) + dy;
                        if (sx < 0 || sy < 0 || sx >= Size || sy >= Size) { lit += 1; continue; }
                        lit += z <= Depth[sy * Size + sx] + bias ? 1 : 0;
                    }
                return lit / 4f;
            }
        }

        private sealed class Lighting
        {
            public Vector3 Sun, SunColor, Ambient, SkyTop, SkyHorizon, Fog;
            public float FogDensity, Saturation, Contrast;
            public Vector3 Tint;
            public bool Night;
        }

        private static Lighting LightingFor(SceneDocument scene, bool marketing)
        {
            var look = PlaceCompiler.LookFor(scene.Lighting);
            bool night = look.ClockTime < 5.5f || look.ClockTime > 19.5f;
            float warm = look.ClockTime is > 16 and < 19.5f || look.ClockTime is > 5.5f and < 8 ? 1 : 0;
            var sunColor = night ? new Vector3(0.55f, 0.6f, 0.85f) * 0.7f : Vector3.Lerp(new Vector3(1.0f, 0.97f, 0.9f), new Vector3(1.0f, 0.78f, 0.55f), warm);
            sunColor *= Math.Clamp(look.Brightness / 2.4f, 0.35f, 1.3f);
            var ambient = (V(look.Outdoor) * 0.55f + V(look.Ambient) * 0.25f) * (night ? 0.9f : 1f);
            return new Lighting
            {
                Sun = night ? Vector3.Normalize(new Vector3(0.45f, 0.75f, 0.45f)) : warm > 0 ? Vector3.Normalize(new Vector3(0.75f, 0.42f, 0.35f)) : SunDirection,
                SunColor = sunColor * 0.85f,
                Ambient = ambient,
                SkyTop = V(look.SkyTop),
                SkyHorizon = V(look.SkyHorizon),
                Fog = Vector3.Lerp(V(look.SkyHorizon), V(look.AtmosphereColor), 0.35f),
                FogDensity = 0.0006f + look.Density * 0.0026f,
                Saturation = look.Saturation + (marketing ? 0.18f : 0.04f),
                Contrast = look.Contrast + (marketing ? 0.08f : 0f),
                Tint = V(look.Tint),
                Night = night,
            };
        }

        // ─────────────────────────────── entry point ───────────────────────────────

        public static byte[] Render(RenderWorld world, RenderRequest request)
        {
            int ss = Math.Clamp(request.Supersample, 1, 3);
            int w = Math.Clamp(request.Width, 64, 3840), h = Math.Clamp(request.Height, 64, 2160);
            int W = w * ss, H = h * ss;
            var light = LightingFor(world.Scene, request.Marketing);
            var (min, max) = PlayBounds(world);
            var view = ResolveView(world, request, min, max, W, H, out string caption, out bool topDown);

            // Opaque geometry: terrain, parts, and models without a picture.
            var opaque = new List<Mesh>();
            var translucent = new List<Mesh>();
            var sprites = new List<(Vector3 Bottom, float Height, float Width, float Depth, SpriteImage Image, Vector3 Center, float Radius, string ObjectId)>();
            if (world.Terrain != null) opaque.Add(TerrainMesh(world.Terrain, 1));
            foreach (var t in world.Scene.Terrain) opaque.Add(FillMesh(t));

            var spriteObjects = SpriteObjects(world);
            foreach (var p in world.Parts)
            {
                if (p.Transparency >= 0.98f) continue;
                if (spriteObjects.ContainsKey(p.ObjectId)) continue;
                var mesh = new Mesh { Alpha = 1 - Math.Clamp(p.Transparency, 0, 0.9f), Emissive = p.Neon };
                var color = MaterialTint(p.Material, V(p.Color));
                switch (p.Shape)
                {
                    case "Ball": AddBall(mesh, p.CFrame, p.Size, color); break;
                    case "Cylinder": AddCylinder(mesh, p.CFrame, p.Size, color); break;
                    case "Wedge": AddWedge(mesh, p.CFrame, p.Size, color); break;
                    default: AddBox(mesh, p.CFrame, p.Size, color); break;
                }
                if (p.Kind == SceneKinds.Light) { mesh.Emissive = true; mesh.CastsShadow = false; }
                (mesh.Alpha < 0.98f ? translucent : opaque).Add(mesh);
            }
            foreach (var (objectId, info) in spriteObjects)
                sprites.Add((info.Bottom, info.Height, info.Width, 0, info.Image, info.Center, info.Radius, objectId));

            // Sun shadows.
            var shadow = BuildShadowMap(opaque, min, max, light.Sun, sprites);

            var frame = new Frame(W, H);
            PaintSky(frame, view, light, topDown);
            foreach (var mesh in opaque) Draw(frame, view, mesh, light, shadow, depthWrite: true);
            // Soft blobs under every pictured model, then the models themselves (far to near).
            foreach (var s in sprites) DrawBlob(frame, view, s.Center with { Y = s.Bottom.Y + 0.08f }, s.Radius * 0.85f, world);
            foreach (var s in sprites.OrderByDescending(s => Vector3.Distance(s.Center, view.Eye)))
                DrawSprite(frame, view, s.Bottom, s.Height, s.Width, s.Image, light, topDown);
            if (world.Terrain?.WaterLevel is float water) DrawWater(frame, view, world.Terrain, water, light);
            foreach (var mesh in translucent.OrderByDescending(m => Vector3.Distance(m.Vertices[0].World, view.Eye)))
                Draw(frame, view, mesh, light, shadow, depthWrite: false);
            if (!topDown) ApplyFog(frame, view, light);
            Grade(frame, light, request.Marketing);

            using var bitmap = Resolve(frame, ss);
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(bitmap, 0, 0);
            if (request.Marketing) Vignette(canvas, w, h);
            if (!request.Marketing)
            {
                var flat = new Camera(view.Eye, view.Eye + view.Forward, view.Orthographic ? 0 : 2 * MathF.Atan(view.Cy / view.Focal) * 180 / MathF.PI, view.Ortho / ss, w, h, view.Up);
                if (topDown) MapOverlay(canvas, flat, world, w, h, request.Labels || request.View.Equals("map", StringComparison.OrdinalIgnoreCase), min, max);
                else if (request.Labels) Labels(canvas, flat, world);
                if (request.Caption && caption.Length > 0) Caption(canvas, caption);
            }
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        // ─────────────────────────────── views ───────────────────────────────

        /// <summary>The area worth framing: the playable rectangle if set, else what is built (ignoring huge floors), else the terrain.</summary>
        public static (Vector3 Min, Vector3 Max) PlayBounds(RenderWorld world)
        {
            var b = world.Scene.Bounds;
            float ground = 0;
            if (b.HasArea)
            {
                float top = world.Parts.Count > 0 ? world.Parts.Max(p => p.CFrame.Position.Y + p.Size.Y / 2) : 30;
                return (new Vector3(b.MinX, ground - 2, b.MinZ), new Vector3(b.MaxX, MathF.Max(top, 20), b.MaxZ));
            }
            var solid = world.Parts.Where(p => p.Size.X < 300 && p.Size.Z < 300).ToList();
            if (solid.Count > 0)
            {
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var p in solid)
                    foreach (var c in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size))
                    {
                        mn = Vector3.Min(mn, V(c));
                        mx = Vector3.Max(mx, V(c));
                    }
                return (mn, mx);
            }
            if (world.Terrain is { } t) return (new Vector3(t.OriginX, 0, t.OriginZ), new Vector3(t.MaxX, 30, t.MaxZ));
            return (new Vector3(-50, 0, -50), new Vector3(50, 20, 50));
        }

        private static Camera ResolveView(RenderWorld world, RenderRequest request, Vector3 min, Vector3 max, int W, int H, out string caption, out bool topDown)
        {
            var center = (min + max) / 2;
            var extent = max - min;
            float radius = MathF.Max(12, new Vector2(extent.X, extent.Z).Length() / 2);
            topDown = false;
            string v = (request.View ?? "iso").Trim();
            if (request.Camera is { } custom)
            {
                caption = "Custom camera";
                return new Camera(custom.Eye, custom.Target, custom.Fov, 1, W, H);
            }
            switch (v.ToLowerInvariant())
            {
                case "overview":
                case "map":
                    {
                        topDown = true;
                        float scale = MathF.Min(W / (extent.X * 1.08f + 16), H / (extent.Z * 1.08f + 16));
                        caption = $"Map from above · {extent.X:0}×{extent.Z:0} studs · −Z (Roblox forward) is up";
                        return new Camera(center + new Vector3(0, 2000, 0), center, 0, scale, W, H, new Vector3(0, 0, -1));
                    }
                case "aerial":
                    caption = "Aerial view";
                    return new Camera(center + new Vector3(radius * 0.55f, radius * 1.45f, radius * 1.15f), center, 50, 1, W, H);
                case "spawn":
                    {
                        var spawn = world.Scene.Objects.FirstOrDefault(o => o.Kind == SceneKinds.Spawn);
                        var at = spawn != null ? new Vector3(spawn.Position[0], spawn.Position[1] + 0.5f, spawn.Position[2]) : center with { Y = 0 };
                        float yaw = (spawn?.Yaw ?? 0) * MathF.PI / 180;
                        var forward = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
                        var head = at + new Vector3(0, 5, 0);
                        caption = "What a player sees on spawning (the default camera, behind the character)";
                        return new Camera(head - forward * 12 + new Vector3(0, 3.5f, 0), head + forward * 18 - new Vector3(0, 1.5f, 0), 70, 1, W, H);
                    }
                case "iso":
                case "":
                    caption = "3/4 view";
                    return new Camera(center + new Vector3(radius * 1.05f, radius * 0.95f, radius * 1.25f), center, 45, 1, W, H);
            }
            if (v.StartsWith("zone:", StringComparison.OrdinalIgnoreCase))
            {
                string id = v[5..].Trim();
                var zone = world.Scene.Layout.Zones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || z.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no zone '{id}' in the layout plan");
                var zc = new Vector3(zone.CenterX, WorldOps.GroundAt(world.Scene, world.Terrain, zone.CenterX, zone.CenterZ), zone.CenterZ);
                float zr = MathF.Max(16, MathF.Max(zone.Area[2] - zone.Area[0], zone.Area[3] - zone.Area[1]) * 0.6f);
                caption = $"Zone {zone.Name}";
                // Look at a zone from the middle of the map, outwards: the world's edge frames it from behind instead of
                // filling the foreground (a zone at the middle gets the usual 3/4 angle).
                var outwards = new Vector2(zc.X - center.X, zc.Z - center.Z);
                var back = outwards.Length() < MathF.Max(8, radius * 0.15f)
                    ? Vector3.Normalize(new Vector3(1.1f, 0, 1.2f))
                    : new Vector3(-Vector2.Normalize(outwards).X, 0, -Vector2.Normalize(outwards).Y);
                return new Camera(zc + back * (zr * 1.55f) + new Vector3(0, zr * 0.9f, 0), zc, 45, 1, W, H);
            }
            if (v.Contains('>'))
            {
                var halves = v.Split('>');
                var eye = ParseVec(halves[0]);
                var target = ParseVec(halves[1]);
                if (eye == null || target == null) throw new ArgumentException("a camera is \"x,y,z>x,y,z\" (from > to)");
                caption = "Custom camera";
                return new Camera(eye.Value, target.Value, 55, 1, W, H);
            }
            var focus = world.Parts.Where(p => p.ObjectId.Equals(v, StringComparison.OrdinalIgnoreCase)).ToList();
            if (focus.Count == 0) throw new ArgumentException($"no object '{v}' in the scene (views: overview, map, iso, aerial, spawn, zone:<id>, an object id, or \"x,y,z>x,y,z\")");
            var fmin = new Vector3(float.MaxValue); var fmax = new Vector3(float.MinValue);
            foreach (var p in focus)
                foreach (var c in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size)) { fmin = Vector3.Min(fmin, V(c)); fmax = Vector3.Max(fmax, V(c)); }
            var fc = (fmin + fmax) / 2;
            float fr = MathF.Max(4, (fmax - fmin).Length() / 2);
            caption = $"Close-up of {v}";
            return new Camera(fc + new Vector3(fr * 1.5f, fr * 1.0f, fr * 1.9f), fc, 45, 1, W, H);
        }

        private static Vector3? ParseVec(string text)
        {
            var parts = text.Trim().Trim('[', ']', '(', ')').Split(',');
            if (parts.Length != 3) return null;
            var nums = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out nums[i])) return null;
            return new Vector3(nums[0], nums[1], nums[2]);
        }

        // ─────────────────────────────── models as pictures ───────────────────────────────

        /// <summary>
        /// Which placed models are drawn as their picture: those built mostly from meshes (whose shape only Roblox can
        /// draw). A model made of plain parts is drawn from its parts, which is exact.
        /// </summary>
        private static Dictionary<string, (Vector3 Bottom, float Height, float Width, Vector3 Center, float Radius, SpriteImage Image)> SpriteObjects(RenderWorld world)
        {
            var map = new Dictionary<string, (Vector3, float, float, Vector3, float, SpriteImage)>(StringComparer.Ordinal);
            foreach (var g in world.Parts.Where(p => p.AssetId != null).GroupBy(p => p.ObjectId))
            {
                var parts = g.ToList();
                float meshVolume = parts.Where(p => p.IsMesh).Sum(p => p.Size.X * p.Size.Y * p.Size.Z);
                float total = parts.Sum(p => p.Size.X * p.Size.Y * p.Size.Z);
                if (total <= 0 || meshVolume / total < 0.25f) continue;
                var image = world.Sprite(parts[0].AssetId!.Value);
                if (image == null) continue;
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var p in parts.Where(p => p.Transparency < 0.98f))
                    foreach (var c in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size)) { mn = Vector3.Min(mn, V(c)); mx = Vector3.Max(mx, V(c)); }
                if (mn.X > mx.X) continue;
                float height = mx.Y - mn.Y;
                var (x0, y0, x1, y1) = image.Opaque;
                float aspect = (x1 - x0 + 1) / (float)Math.Max(1, y1 - y0 + 1);
                float width = MathF.Min(height * aspect, MathF.Max(mx.X - mn.X, mx.Z - mn.Z) * 1.45f);
                if (width < 0.5f) width = height * aspect;
                var center = (mn + mx) / 2;
                map[g.Key] = (new Vector3(center.X, mn.Y, center.Z), height, width, center, MathF.Max(mx.X - mn.X, mx.Z - mn.Z) / 2, image);
            }
            return map;
        }

        // ─────────────────────────────── terrain ───────────────────────────────

        private static Mesh TerrainMesh(TerrainMap t, int step)
        {
            var mesh = new Mesh { DepthBias = 0.08f };
            int cols = t.Width + 1, rows = t.Depth + 1;
            var heights = new float[cols * rows];
            var colors = new Vector3[cols * rows];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    float sum = 0; var col = Vector3.Zero; int n = 0;
                    for (int dr = -1; dr <= 0; dr++)
                        for (int dc = -1; dc <= 0; dc++)
                        {
                            int cc = c + dc, rr = r + dr;
                            if (cc < 0 || rr < 0 || cc >= t.Width || rr >= t.Depth) continue;
                            int i = rr * t.Width + cc;
                            sum += t.Heights[i];
                            col += TerrainColors.TryGetValue(t.MaterialNames[t.Materials[i]], out var tc) ? tc : TerrainColors["Grass"];
                            n++;
                        }
                    heights[r * cols + c] = n > 0 ? sum / n : t.Floor;
                    float x = t.OriginX + c * t.Cell, z = t.OriginZ + r * t.Cell;
                    float variation = 1 + TerrainBuilder.Fbm(x / 9f, z / 9f, 7, 2) * 0.07f;
                    colors[r * cols + c] = (n > 0 ? col / n : TerrainColors["Grass"]) * variation;
                }
            Vector3 P(int c, int r) => new(t.OriginX + c * t.Cell, heights[r * cols + c], t.OriginZ + r * t.Cell);
            Vector3 N(int c, int r)
            {
                float hl = heights[r * cols + Math.Max(0, c - 1)], hr = heights[r * cols + Math.Min(cols - 1, c + 1)];
                float hd = heights[Math.Max(0, r - 1) * cols + c], hu = heights[Math.Min(rows - 1, r + 1) * cols + c];
                return Vector3.Normalize(new Vector3(hl - hr, 2 * t.Cell, hd - hu));
            }
            for (int r = 0; r < rows - 1; r += step)
                for (int c = 0; c < cols - 1; c += step)
                {
                    int c1 = Math.Min(cols - 1, c + step), r1 = Math.Min(rows - 1, r + step);
                    var a = new Vertex { World = P(c, r), Normal = N(c, r), Color = colors[r * cols + c] };
                    var b = new Vertex { World = P(c1, r), Normal = N(c1, r), Color = colors[r * cols + c1] };
                    var d = new Vertex { World = P(c1, r1), Normal = N(c1, r1), Color = colors[r1 * cols + c1] };
                    var e = new Vertex { World = P(c, r1), Normal = N(c, r1), Color = colors[r1 * cols + c] };
                    mesh.Tri(a, e, d);
                    mesh.Tri(a, d, b);
                }
            return mesh;
        }

        private static Mesh FillMesh(TerrainFill fill)
        {
            var mesh = new Mesh();
            var color = TerrainColors.TryGetValue(fill.Material, out var c) ? c : TerrainColors["Grass"];
            var cf = new RbxCFrame(new RbxVector3(fill.Center[0], fill.Center[1], fill.Center[2]));
            var size = new RbxVector3(fill.Size[0], fill.Size[1], fill.Size[2]);
            if (fill.Shape == "ball") AddBall(mesh, cf, new RbxVector3(MathF.Max(size.X, MathF.Max(size.Y, size.Z)), MathF.Max(size.X, MathF.Max(size.Y, size.Z)), MathF.Max(size.X, MathF.Max(size.Y, size.Z))), color);
            else AddBox(mesh, cf, size, color);
            if (fill.Material.Equals("Water", StringComparison.OrdinalIgnoreCase)) mesh.Alpha = 0.7f;
            return mesh;
        }

        // ─────────────────────────────── shadows ───────────────────────────────

        private static ShadowMap? BuildShadowMap(List<Mesh> meshes, Vector3 min, Vector3 max, Vector3 sun,
            List<(Vector3 Bottom, float Height, float Width, float Depth, SpriteImage Image, Vector3 Center, float Radius, string ObjectId)> sprites)
        {
            var center = (min + max) / 2;
            float radius = MathF.Max(20, (max - min).Length() / 2 + 20);
            var map = new ShadowMap(2048);
            map.Forward = -sun;
            map.Right = Vector3.Normalize(Vector3.Cross(map.Forward, Vector3.UnitY));
            map.Up = Vector3.Cross(map.Right, map.Forward);
            map.Origin = center - map.Forward * (radius * 3);
            map.Scale = map.Size / (radius * 2.1f);
            foreach (var mesh in meshes.Where(m => m.CastsShadow))
            {
                for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    var (ax, ay, az) = map.Project(mesh.Vertices[mesh.Indices[i]].World);
                    var (bx, by, bz) = map.Project(mesh.Vertices[mesh.Indices[i + 1]].World);
                    var (cx, cy, cz) = map.Project(mesh.Vertices[mesh.Indices[i + 2]].World);
                    RasterDepth(map.Depth, map.Size, map.Size, ax, ay, az, bx, by, bz, cx, cy, cz);
                }
            }
            return map;
        }

        private static void RasterDepth(float[] depth, int w, int h, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
        {
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (MathF.Abs(area) < 1e-6f || x0 > x1 || y0 > y1) return;
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                    float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * az + w1 * bz + w2 * cz;
                    int i = y * w + x;
                    if (z < depth[i]) depth[i] = z;
                }
            }
        }

        // ─────────────────────────────── drawing ───────────────────────────────

        private static void PaintSky(Frame frame, Camera view, Lighting light, bool topDown)
        {
            if (topDown)
            {
                var ground = new Vector3(0.16f, 0.2f, 0.26f);
                Array.Fill(frame.Color, ground);
                return;
            }
            for (int y = 0; y < frame.H; y++)
            {
                for (int x = 0; x < frame.W; x++)
                {
                    Vector3 dir = view.Orthographic ? view.Forward
                        : Vector3.Normalize(view.Forward * view.Focal + view.Right * (x - view.Cx) - view.Up * (y - view.Cy));
                    float elevation = Math.Clamp(dir.Y, -0.2f, 1);
                    float t = MathF.Pow(Math.Clamp(elevation * 1.6f, 0, 1), 0.6f);
                    var sky = Vector3.Lerp(light.SkyHorizon, light.SkyTop, t);
                    float sunDot = Vector3.Dot(dir, light.Sun);
                    if (sunDot > 0.9f) sky += (light.Night ? new Vector3(0.5f, 0.55f, 0.75f) : new Vector3(1f, 0.92f, 0.75f)) * MathF.Pow((sunDot - 0.9f) / 0.1f, 3) * 0.55f;
                    frame.Color[y * frame.W + x] = sky;
                }
            }
        }

        private static Vector3 Shade(Vector3 albedo, Vector3 normal, Vector3 world, Lighting light, ShadowMap? shadow, bool emissive)
        {
            if (emissive) return albedo * 1.25f + new Vector3(0.08f);
            float nDotL = MathF.Max(0, Vector3.Dot(normal, light.Sun));
            float lit = nDotL > 0 && shadow != null ? shadow.Light(world, normal) : 1;
            // Sky light from above, a little bounce from below: faces never go black.
            float sky = 0.65f + 0.35f * normal.Y;
            return albedo * (light.Ambient * sky + light.SunColor * nDotL * (0.25f + 0.75f * lit));
        }

        private static void Draw(Frame frame, Camera view, Mesh mesh, Lighting light, ShadowMap? shadow, bool depthWrite)
        {
            var verts = mesh.Vertices;
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                var a = verts[mesh.Indices[i]]; var b = verts[mesh.Indices[i + 1]]; var c = verts[mesh.Indices[i + 2]];
                var ca = view.ToCamera(a.World); var cb = view.ToCamera(b.World); var cc = view.ToCamera(c.World);
                if (!view.Orthographic)
                {
                    if (ca.Z < Camera.Near && cb.Z < Camera.Near && cc.Z < Camera.Near) continue;
                    if (ca.Z < Camera.Near || cb.Z < Camera.Near || cc.Z < Camera.Near)
                    {
                        foreach (var (pa, pb, pc) in ClipNear((a, ca), (b, cb), (c, cc)))
                            Triangle(frame, view, mesh, pa, pb, pc, light, shadow, depthWrite);
                        continue;
                    }
                }
                Triangle(frame, view, mesh, (a, ca), (b, cb), (c, cc), light, shadow, depthWrite);
            }
        }

        private static IEnumerable<((Vertex V, Vector3 C), (Vertex V, Vector3 C), (Vertex V, Vector3 C))> ClipNear((Vertex V, Vector3 C) a, (Vertex V, Vector3 C) b, (Vertex V, Vector3 C) c)
        {
            var input = new[] { a, b, c };
            var output = new List<(Vertex V, Vector3 C)>();
            for (int i = 0; i < 3; i++)
            {
                var cur = input[i]; var next = input[(i + 1) % 3];
                bool curIn = cur.C.Z >= Camera.Near, nextIn = next.C.Z >= Camera.Near;
                if (curIn) output.Add(cur);
                if (curIn != nextIn)
                {
                    float t = (Camera.Near - cur.C.Z) / (next.C.Z - cur.C.Z);
                    var v = new Vertex
                    {
                        World = Vector3.Lerp(cur.V.World, next.V.World, t),
                        Normal = Vector3.Normalize(Vector3.Lerp(cur.V.Normal, next.V.Normal, t)),
                        Color = Vector3.Lerp(cur.V.Color, next.V.Color, t),
                        Uv = Vector2.Lerp(cur.V.Uv, next.V.Uv, t),
                    };
                    output.Add((v, Vector3.Lerp(cur.C, next.C, t)));
                }
            }
            for (int i = 1; i + 1 < output.Count; i++) yield return (output[0], output[i], output[i + 1]);
        }

        private static void Triangle(Frame frame, Camera view, Mesh mesh, (Vertex V, Vector3 C) a, (Vertex V, Vector3 C) b, (Vertex V, Vector3 C) c,
            Lighting light, ShadowMap? shadow, bool depthWrite)
        {
            var sa = view.Screen(a.C); var sb = view.Screen(b.C); var sc = view.Screen(c.C);
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sb.Y - sa.Y) * (sc.X - sa.X);
            if (MathF.Abs(area) < 1e-4f) return;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X)))), x1 = Math.Min(frame.W - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y)))), y1 = Math.Min(frame.H - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
            if (x0 > x1 || y0 > y1) return;
            bool persp = !view.Orthographic;
            float ia = persp ? 1 / a.C.Z : 1, ib = persp ? 1 / b.C.Z : 1, ic = persp ? 1 / c.C.Z : 1;
            // Flat faces (parts) light once per triangle unless shadows vary across them.
            bool smooth = a.V.Normal != b.V.Normal || b.V.Normal != c.V.Normal;
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float w0 = ((sb.X - px) * (sc.Y - py) - (sb.Y - py) * (sc.X - px)) / area;
                    float w1 = ((sc.X - px) * (sa.Y - py) - (sc.Y - py) * (sa.X - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    float pw0 = w0 * ia, pw1 = w1 * ib, pw2 = w2 * ic;
                    float sum = pw0 + pw1 + pw2;
                    pw0 /= sum; pw1 /= sum; pw2 /= sum;
                    float z = pw0 * a.C.Z + pw1 * b.C.Z + pw2 * c.C.Z;
                    if (mesh.DepthBias > 0) z += mesh.DepthBias + z * 0.0006f;
                    int idx = y * frame.W + x;
                    if (z >= frame.Depth[idx]) continue;
                    var world = a.V.World * pw0 + b.V.World * pw1 + c.V.World * pw2;
                    var normal = smooth ? Vector3.Normalize(a.V.Normal * pw0 + b.V.Normal * pw1 + c.V.Normal * pw2) : a.V.Normal;
                    var albedo = a.V.Color * pw0 + b.V.Color * pw1 + c.V.Color * pw2;
                    var color = Shade(albedo, normal, world, light, shadow, mesh.Emissive);
                    if (mesh.Alpha < 0.999f) color = Vector3.Lerp(frame.Color[idx], color, mesh.Alpha);
                    frame.Color[idx] = color;
                    if (depthWrite) frame.Depth[idx] = z;
                }
            }
        }

        private static void DrawBlob(Frame frame, Camera view, Vector3 center, float radius, RenderWorld world)
        {
            if (radius < 0.5f) return;
            const int segments = 20;
            var ring = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = i * MathF.Tau / segments;
                float x = center.X + MathF.Cos(a) * radius, z = center.Z + MathF.Sin(a) * radius;
                ring[i] = new Vector3(x, MathF.Max(center.Y, WorldOps.GroundAt(world.Scene, world.Terrain, x, z)) + 0.1f, z);
            }
            var cc = view.ToCamera(center);
            if (!view.Visible(cc)) return;
            var sCenter = view.Screen(cc);
            for (int i = 0; i < segments; i++)
            {
                var c1 = view.ToCamera(ring[i]); var c2 = view.ToCamera(ring[(i + 1) % segments]);
                if (!view.Visible(c1) || !view.Visible(c2)) continue;
                var s1 = view.Screen(c1); var s2 = view.Screen(c2);
                BlobTriangle(frame, sCenter, cc.Z, s1, c1.Z, s2, c2.Z);
            }
        }

        private static void BlobTriangle(Frame frame, Vector2 sa, float za, Vector2 sb, float zb, Vector2 sc, float zc)
        {
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sb.Y - sa.Y) * (sc.X - sa.X);
            if (MathF.Abs(area) < 1e-4f) return;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X)))), x1 = Math.Min(frame.W - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y)))), y1 = Math.Min(frame.H - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((sb.X - px) * (sc.Y - py) - (sb.Y - py) * (sc.X - px)) / area;
                    float w1 = ((sc.X - px) * (sa.Y - py) - (sc.Y - py) * (sa.X - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * za + w1 * zb + w2 * zc;
                    int idx = y * frame.W + x;
                    if (z > frame.Depth[idx] + 1.5f) continue; // hidden behind something
                    // Darkest under the centre (w0 is the centre's weight), fading to nothing at the rim.
                    float strength = 0.42f * MathF.Pow(w0, 0.8f);
                    frame.Color[idx] *= 1 - strength;
                }
        }

        private static void DrawSprite(Frame frame, Camera view, Vector3 bottom, float height, float width, SpriteImage image, Lighting light, bool topDown)
        {
            var (ox0, oy0, ox1, oy1) = image.Opaque;
            float u0 = ox0 / (float)image.Width, u1 = (ox1 + 1) / (float)image.Width, v0 = oy0 / (float)image.Height, v1 = (oy1 + 1) / (float)image.Height;
            Vector3 p00, p10, p11, p01; // corners: bottom-left, bottom-right, top-right, top-left
            if (topDown)
            {
                // From above, the picture lies flat over the model's footprint (a map icon).
                float half = MathF.Max(width, height) * 0.5f;
                float y = bottom.Y + height;
                p00 = new Vector3(bottom.X - half, y, bottom.Z + half);
                p10 = new Vector3(bottom.X + half, y, bottom.Z + half);
                p11 = new Vector3(bottom.X + half, y, bottom.Z - half);
                p01 = new Vector3(bottom.X - half, y, bottom.Z - half);
            }
            else
            {
                var right = new Vector3(view.Right.X, 0, view.Right.Z);
                right = right.LengthSquared() < 1e-4f ? view.Right : Vector3.Normalize(right);
                // The picture stands on a plane through the model's middle, facing the camera.
                var toCamera = new Vector3(view.Eye.X - bottom.X, 0, view.Eye.Z - bottom.Z);
                var shift = toCamera.LengthSquared() > 1e-4f ? Vector3.Normalize(toCamera) * MathF.Min(width * 0.15f, 2f) : Vector3.Zero;
                var b = bottom + shift;
                p00 = b - right * (width / 2);
                p10 = b + right * (width / 2);
                p11 = p10 + new Vector3(0, height, 0);
                p01 = p00 + new Vector3(0, height, 0);
            }
            var tint = Vector3.Clamp((light.Ambient * 0.9f + light.SunColor * 0.75f) / 1.05f, new Vector3(0.45f), new Vector3(1.15f));
            SpriteTriangle(frame, view, image, tint, (p00, new Vector2(u0, v1)), (p10, new Vector2(u1, v1)), (p11, new Vector2(u1, v0)));
            SpriteTriangle(frame, view, image, tint, (p00, new Vector2(u0, v1)), (p11, new Vector2(u1, v0)), (p01, new Vector2(u0, v0)));
        }

        private static void SpriteTriangle(Frame frame, Camera view, SpriteImage image, Vector3 tint, (Vector3 P, Vector2 Uv) a, (Vector3 P, Vector2 Uv) b, (Vector3 P, Vector2 Uv) c)
        {
            var ca = view.ToCamera(a.P); var cb = view.ToCamera(b.P); var cc = view.ToCamera(c.P);
            if (!view.Visible(ca) || !view.Visible(cb) || !view.Visible(cc)) return;
            var sa = view.Screen(ca); var sb = view.Screen(cb); var sc = view.Screen(cc);
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sb.Y - sa.Y) * (sc.X - sa.X);
            if (MathF.Abs(area) < 1e-4f) return;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X)))), x1 = Math.Min(frame.W - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y)))), y1 = Math.Min(frame.H - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
            bool persp = !view.Orthographic;
            float ia = persp ? 1 / ca.Z : 1, ib = persp ? 1 / cb.Z : 1, ic = persp ? 1 / cc.Z : 1;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((sb.X - px) * (sc.Y - py) - (sb.Y - py) * (sc.X - px)) / area;
                    float w1 = ((sc.X - px) * (sa.Y - py) - (sc.Y - py) * (sa.X - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    float pw0 = w0 * ia, pw1 = w1 * ib, pw2 = w2 * ic;
                    float sum = pw0 + pw1 + pw2;
                    pw0 /= sum; pw1 /= sum; pw2 /= sum;
                    float z = pw0 * ca.Z + pw1 * cb.Z + pw2 * cc.Z;
                    int idx = y * frame.W + x;
                    if (z >= frame.Depth[idx]) continue;
                    var uv = a.Uv * pw0 + b.Uv * pw1 + c.Uv * pw2;
                    var texel = image.Sample(uv.X, uv.Y);
                    if (texel.W < 0.04f) continue;
                    var color = new Vector3(texel.X, texel.Y, texel.Z) * tint;
                    frame.Color[idx] = Vector3.Lerp(frame.Color[idx], color, Math.Clamp(texel.W * 1.15f, 0, 1));
                    if (texel.W > 0.5f) frame.Depth[idx] = z;
                }
        }

        private static void DrawWater(Frame frame, Camera view, TerrainMap t, float level, Lighting light)
        {
            var water = new Vector3(0.22f, 0.62f, 0.88f);
            var mesh = new Mesh { Alpha = 0.72f, CastsShadow = false };
            for (int r = 0; r < t.Depth; r++)
                for (int c = 0; c < t.Width; c++)
                {
                    if (t.Heights[r * t.Width + c] >= level - 0.05f) continue;
                    float x = t.OriginX + c * t.Cell, z = t.OriginZ + r * t.Cell, s = t.Cell;
                    var n = Vector3.UnitY;
                    mesh.Quad(Vx(new Vector3(x, level, z), n, water), Vx(new Vector3(x, level, z + s), n, water), Vx(new Vector3(x + s, level, z + s), n, water), Vx(new Vector3(x + s, level, z), n, water));
                }
            if (mesh.Indices.Count > 0) Draw(frame, view, mesh, light, null, depthWrite: false);
        }

        private static void ApplyFog(Frame frame, Camera view, Lighting light)
        {
            for (int i = 0; i < frame.Color.Length; i++)
            {
                float z = frame.Depth[i];
                if (z == float.MaxValue) continue;
                float f = 1 - MathF.Exp(-MathF.Max(0, z - view.FogStart) * light.FogDensity);
                frame.Color[i] = Vector3.Lerp(frame.Color[i], light.Fog, Math.Clamp(f, 0, 0.85f));
            }
        }

        private static void Grade(Frame frame, Lighting light, bool marketing)
        {
            float sat = 1 + light.Saturation, contrast = 1 + light.Contrast;
            var tint = light.Tint;
            for (int i = 0; i < frame.Color.Length; i++)
            {
                var c = frame.Color[i] * tint;
                float luma = c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
                c = Vector3.Lerp(new Vector3(luma), c, sat);
                c = (c - new Vector3(0.5f)) * contrast + new Vector3(0.5f);
                if (marketing) c *= 1.04f;
                frame.Color[i] = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
            }
        }

        private static SKBitmap Resolve(Frame frame, int ss)
        {
            int w = frame.W / ss, h = frame.H / ss;
            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var pixels = new byte[w * h * 4];
            float inv = 1f / (ss * ss);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var sum = Vector3.Zero;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                            sum += frame.Color[(y * ss + sy) * frame.W + x * ss + sx];
                    sum *= inv;
                    int i = (y * w + x) * 4;
                    pixels[i] = (byte)Math.Clamp(sum.X * 255f + 0.5f, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(sum.Y * 255f + 0.5f, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(sum.Z * 255f + 0.5f, 0, 255);
                    pixels[i + 3] = 255;
                }
            var target = bitmap.GetPixels();
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * w * 4, target + y * bitmap.RowBytes, w * 4);
            bitmap.NotifyPixelsChanged();
            return bitmap;
        }

        // ─────────────────────────────── overlays ───────────────────────────────

        private static SKTypeface Bold => SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

        private static void Vignette(SKCanvas canvas, int w, int h)
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(new SKPoint(w / 2f, h / 2f), MathF.Max(w, h) * 0.75f,
                    new[] { SKColors.Transparent, new SKColor(0, 0, 0, 70) }, new[] { 0.6f, 1f }, SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(new SKRect(0, 0, w, h), paint);
        }

        private static void Caption(SKCanvas canvas, string caption)
        {
            using var font = new SKFont(Bold, 15);
            using var ink = new SKPaint { Color = new SKColor(25, 30, 40), IsAntialias = true };
            using var back = new SKPaint { Color = new SKColor(255, 255, 255, 215), IsAntialias = true };
            float w = font.MeasureText(caption) + 16;
            canvas.DrawRoundRect(new SKRect(8, 8, 8 + w, 34), 7, 7, back);
            canvas.DrawText(caption, 16, 27, SKTextAlign.Left, font, ink);
        }

        private static SKPoint Pt(Camera cam, Vector3 world)
        {
            var s = cam.Screen(cam.ToCamera(world));
            return new SKPoint(s.X, s.Y);
        }

        /// <summary>
        /// Object ids on the picture, without burying it: individual objects (spawns, signs, buildings, landmarks — what
        /// an agent refers to by id) get their id where it fits, tallest first; a big set of the same thing (a scattered
        /// forest, a fence) gets one label at its middle, "Pine Tree ×70".
        /// </summary>
        private static void Labels(SKCanvas canvas, Camera cam, RenderWorld world)
        {
            using var font = new SKFont(Bold, 12);
            using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var back = new SKPaint { Color = new SKColor(20, 24, 40, 190), IsAntialias = true };
            using var setBack = new SKPaint { Color = new SKColor(30, 110, 60, 210), IsAntialias = true };
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in world.Scene.Objects) names.TryAdd(o.Id, o.Name);
            var objects = world.Parts.GroupBy(p => p.ObjectId).Select(g => (
                Id: g.Key,
                Name: names.TryGetValue(g.Key, out var n) ? n : g.First().Name,
                Top: g.Select(p => new Vector3(p.CFrame.Position.X, p.CFrame.Position.Y + p.Size.Y / 2, p.CFrame.Position.Z)).OrderByDescending(v => v.Y).First())).ToList();
            var sets = objects.Count <= 40 ? new Dictionary<string, List<Vector3>>()
                : objects.GroupBy(o => o.Name).Where(s => s.Count() >= 6).ToDictionary(s => s.Key, s => s.Select(o => o.Top).ToList());
            var taken = new List<SKRect>();
            void Draw(string text, Vector3 at, SKPaint fill)
            {
                var c = cam.ToCamera(at);
                if (!cam.Visible(c)) return;
                var s = cam.Screen(c);
                float w = font.MeasureText(text) + 8;
                var rect = new SKRect(s.X - w / 2, s.Y - 18, s.X + w / 2, s.Y - 3);
                if (taken.Any(r => r.IntersectsWith(rect))) return;
                taken.Add(rect);
                canvas.DrawRoundRect(rect, 4, 4, fill);
                canvas.DrawText(text, s.X, s.Y - 6, SKTextAlign.Center, font, ink);
            }
            foreach (var o in objects.Where(o => !sets.ContainsKey(o.Name)).OrderByDescending(o => o.Top.Y))
                Draw(o.Id, o.Top, back);
            foreach (var (name, tops) in sets)
                Draw($"{name} ×{tops.Count}", new Vector3(tops.Average(t => t.X), tops.Max(t => t.Y), tops.Average(t => t.Z)), setBack);
        }

        private static readonly SKColor[] ZoneColors =
        {
            new(255, 196, 61), new(92, 200, 255), new(160, 120, 255), new(255, 120, 160), new(110, 230, 140), new(255, 150, 80), new(80, 220, 210),
        };

        /// <summary>The map overlay: grid with coordinates, the playable area, zones, paths, landmarks, the spawn and its facing, labels, a scale bar.</summary>
        private static void MapOverlay(SKCanvas canvas, Camera cam, RenderWorld world, int w, int h, bool labels, Vector3 min, Vector3 max)
        {
            float span = MathF.Max(max.X - min.X, max.Z - min.Z);
            float step = span <= 80 ? 10 : span <= 200 ? 25 : span <= 500 ? 50 : 100;
            using (var grid = new SKPaint { Color = new SKColor(255, 255, 255, 38), StrokeWidth = 1, IsAntialias = true })
            using (var font = new SKFont(Bold, 11))
            using (var ink = new SKPaint { Color = new SKColor(255, 255, 255, 190), IsAntialias = true })
            {
                for (float x = MathF.Floor((min.X - span) / step) * step; x <= max.X + span; x += step)
                {
                    var a = Pt(cam, new Vector3(x, 0, min.Z - span)); var b = Pt(cam, new Vector3(x, 0, max.Z + span));
                    if (a.X < -10 || a.X > w + 10) continue;
                    canvas.DrawLine(a.X, 0, b.X, h, grid);
                    canvas.DrawText(x.ToString("0"), a.X + 3, h - 6, SKTextAlign.Left, font, ink);
                }
                for (float z = MathF.Floor((min.Z - span) / step) * step; z <= max.Z + span; z += step)
                {
                    var a = Pt(cam, new Vector3(min.X - span, 0, z));
                    if (a.Y < -10 || a.Y > h + 10) continue;
                    canvas.DrawLine(0, a.Y, w, a.Y, grid);
                    canvas.DrawText("z " + z.ToString("0"), 4, a.Y - 3, SKTextAlign.Left, font, ink);
                }
            }

            var bounds = world.Scene.Bounds;
            if (bounds.HasArea)
            {
                var a = Pt(cam, new Vector3(bounds.MinX, 0, bounds.MinZ)); var b = Pt(cam, new Vector3(bounds.MaxX, 0, bounds.MaxZ));
                using var edge = new SKPaint { Color = new SKColor(255, 80, 80, 220), StrokeWidth = 2.5f, Style = SKPaintStyle.Stroke, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 10f, 6f }, 0) };
                canvas.DrawRect(SKRect.Create(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y)), edge);
            }

            int zi = 0;
            using var zoneFont = new SKFont(Bold, 14);
            foreach (var zone in world.Scene.Layout.Zones.OrderBy(z => z.Order))
            {
                var color = ZoneColors[zi++ % ZoneColors.Length];
                var a = Pt(cam, new Vector3(zone.Area[0], 0, zone.Area[1])); var b = Pt(cam, new Vector3(zone.Area[2], 0, zone.Area[3]));
                var rect = SKRect.Create(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
                using (var fill = new SKPaint { Color = color.WithAlpha(40), IsAntialias = true }) canvas.DrawRoundRect(rect, 6, 6, fill);
                using (var line = new SKPaint { Color = color.WithAlpha(230), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true }) canvas.DrawRoundRect(rect, 6, 6, line);
                string text = $"{zone.Order}. {zone.Name} ({zone.Id})";
                using var back = new SKPaint { Color = color.WithAlpha(230), IsAntialias = true };
                using var ink = new SKPaint { Color = new SKColor(20, 20, 30), IsAntialias = true };
                float tw = zoneFont.MeasureText(text) + 10;
                canvas.DrawRoundRect(new SKRect(rect.Left + 4, rect.Top + 4, rect.Left + 4 + tw, rect.Top + 24), 5, 5, back);
                canvas.DrawText(text, rect.Left + 9, rect.Top + 19, SKTextAlign.Left, zoneFont, ink);
            }

            using (var path = new SKPaint { Color = new SKColor(255, 255, 255, 200), StrokeWidth = 3, Style = SKPaintStyle.Stroke, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0) })
                foreach (var p in world.Scene.Layout.Paths.Where(p => p.Points.Count >= 2))
                {
                    using var skPath = new SKPath();
                    skPath.MoveTo(Pt(cam, new Vector3(p.Points[0][0], 0, p.Points[0][1])));
                    foreach (var pt in p.Points.Skip(1)) skPath.LineTo(Pt(cam, new Vector3(pt[0], 0, pt[1])));
                    canvas.DrawPath(skPath, path);
                }

            // Landmarks: a gold marker (drawn, not a glyph: the label font has no star) and the name on a dark tab.
            using (var star = new SKPaint { Color = new SKColor(255, 230, 90), IsAntialias = true })
            using (var rim = new SKPaint { Color = new SKColor(20, 24, 40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            using (var font = new SKFont(Bold, 12))
            using (var tab = new SKPaint { Color = new SKColor(20, 24, 40, 200), IsAntialias = true })
            using (var ink = new SKPaint { Color = new SKColor(255, 230, 90), IsAntialias = true })
                foreach (var l in world.Scene.Layout.Landmarks)
                {
                    var s = Pt(cam, new Vector3(l.Position[0], 0, l.Position[1]));
                    canvas.DrawCircle(s, 6, star);
                    canvas.DrawCircle(s, 6, rim);
                    float width = font.MeasureText(l.Name) + 10;
                    canvas.DrawRoundRect(new SKRect(s.X + 9, s.Y - 9, s.X + 9 + width, s.Y + 8), 4, 4, tab);
                    canvas.DrawText(l.Name, s.X + 14, s.Y + 4, SKTextAlign.Left, font, ink);
                }

            foreach (var spawn in world.Scene.Objects.Where(o => o.Kind == SceneKinds.Spawn))
            {
                var s = Pt(cam, new Vector3(spawn.Position[0], 0, spawn.Position[2]));
                float yaw = spawn.Yaw * MathF.PI / 180;
                var tip = Pt(cam, new Vector3(spawn.Position[0] - MathF.Sin(yaw) * 18, 0, spawn.Position[2] - MathF.Cos(yaw) * 18));
                using var green = new SKPaint { Color = new SKColor(60, 230, 110), IsAntialias = true, StrokeWidth = 4, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
                using var dot = new SKPaint { Color = new SKColor(60, 230, 110), IsAntialias = true };
                canvas.DrawCircle(s, 8, dot);
                canvas.DrawLine(s, tip, green);
                using var font = new SKFont(Bold, 12);
                using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
                canvas.DrawText("SPAWN " + spawn.Id, s.X + 10, s.Y - 8, SKTextAlign.Left, font, ink);
            }

            if (labels) Labels(canvas, cam, world);

            // Scale bar.
            var s0 = Pt(cam, new Vector3(0, 0, 0)); var s1 = Pt(cam, new Vector3(step, 0, 0));
            float px = MathF.Abs(s1.X - s0.X);
            using (var bar = new SKPaint { Color = SKColors.White, StrokeWidth = 3, IsAntialias = true })
            using (var font = new SKFont(Bold, 12))
            using (var ink = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                canvas.DrawLine(w - 20 - px, h - 22, w - 20, h - 22, bar);
                canvas.DrawText($"{step:0} studs", w - 20 - px / 2, h - 28, SKTextAlign.Center, font, ink);
            }
        }
    }
}
