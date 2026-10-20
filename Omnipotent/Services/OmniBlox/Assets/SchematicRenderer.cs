using Omnipotent.Services.OmniBlox.Rbx;
using SkiaSharp;

namespace Omnipotent.Services.OmniBlox.Assets
{
    /// <summary>One solid to draw: a part's world frame, size, shape and look.</summary>
    public sealed record RenderPrimitive(RbxCFrame CFrame, RbxVector3 Size, string Shape, RbxColor3uint8 Color, float Transparency, bool IsMesh, string? Label = null);

    public sealed class RenderCamera
    {
        public RbxVector3 Eye { get; init; }
        public RbxVector3 Target { get; init; }
        public RbxVector3 Up { get; init; } = new(0, 1, 0);
        /// <summary>Vertical field of view in degrees; 0 means orthographic.</summary>
        public float FieldOfView { get; init; } = 40;
        /// <summary>Pixels per stud for orthographic cameras.</summary>
        public float OrthoScale { get; init; } = 10;
        public string Caption { get; init; } = "";
    }

    public sealed class RenderOptions
    {
        public int Width { get; init; } = 512;
        public int Height { get; init; } = 512;
        public bool Grid { get; init; } = true;
        public float GridStep { get; init; } = 4;
        public float GroundY { get; init; }
        public bool FrontArrow { get; init; }
        public RbxVector3 FrontArrowOrigin { get; init; }
        public RbxColor3uint8 Background { get; init; } = new(232, 236, 241);
        public bool Labels { get; init; }
        /// <summary>Paint a sky gradient instead of the flat background (scene and marketing renders).</summary>
        public bool Sky { get; init; }
        public RbxColor3uint8 SkyTop { get; init; } = new(92, 160, 230);
        public RbxColor3uint8 SkyBottom { get; init; } = new(205, 230, 250);
    }

    /// <summary>
    /// A small software renderer for parts: flat-shaded boxes, wedges, cylinders and balls drawn back to
    /// front (painter's algorithm) with SkiaSharp. Mesh parts are drawn as their bounding boxes, outlined, so
    /// a picture always says where the solid is even when its true shape is a mesh. Good enough to show a
    /// vision model size, proportion, colour and which way a model faces — not a substitute for Roblox's own
    /// thumbnail, which the labeller sees alongside.
    /// </summary>
    public static class SchematicRenderer
    {
        private static readonly RbxVector3 LightDirection = new RbxVector3(-0.45f, 0.85f, -0.35f).Unit;

        /// <summary>A face in camera space (x right, y up, z forward), already clipped to the near plane.</summary>
        private sealed record Face(RbxVector3[] Camera, float Depth, SKColor Fill, bool Outline, string? Label, SKPoint LabelAt);

        public static byte[] RenderPng(IReadOnlyList<RenderPrimitive> parts, RenderCamera camera, RenderOptions options)
        {
            using var surface = SKSurface.Create(new SKImageInfo(options.Width, options.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(options.Background.R, options.Background.G, options.Background.B));
            if (options.Sky)
            {
                using var sky = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, options.Height),
                        new[] { new SKColor(options.SkyTop.R, options.SkyTop.G, options.SkyTop.B), new SKColor(options.SkyBottom.R, options.SkyBottom.G, options.SkyBottom.B) },
                        null, SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(new SKRect(0, 0, options.Width, options.Height), sky);
            }

            var view = new View(camera, options.Width, options.Height);
            if (options.Grid) DrawGrid(canvas, view, parts, options);

            var faces = new List<Face>();
            foreach (var p in parts) AddFaces(faces, p, view, options.Labels);

            // A depth buffer, not painter's sorting: a big floor's centre can be nearer the camera than a
            // part standing on its far half, and sorting by centre would paint the floor over that part.
            // Opaque faces first; see-through ones after, far to near, tested against the opaque depth.
            var raster = new Raster(options.Width, options.Height);
            foreach (var f in faces.Where(f => f.Fill.Alpha == 255)) raster.Fill(view, f, depthWrite: true);
            foreach (var f in faces.Where(f => f.Fill.Alpha < 255).OrderByDescending(f => f.Depth)) raster.Fill(view, f, depthWrite: false);
            using (var layer = raster.Resolve()) canvas.DrawBitmap(layer, 0, 0);

            if (options.Labels)
            {
                using var font = new SKFont(SKTypeface.Default, 11);
                using var ink = new SKPaint { Color = new SKColor(20, 20, 20), IsAntialias = true };
                foreach (var f in faces.Where(f => f.Label != null).GroupBy(f => f.Label).Select(g => g.OrderBy(x => x.Depth).First()))
                    canvas.DrawText(f.Label!, f.LabelAt.X, f.LabelAt.Y, SKTextAlign.Center, font, ink);
            }

            if (options.FrontArrow) DrawFrontArrow(canvas, view, options, parts);

            if (!string.IsNullOrEmpty(camera.Caption))
            {
                using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, 15);
                using var ink = new SKPaint { Color = new SKColor(25, 30, 40), IsAntialias = true };
                using var back = new SKPaint { Color = new SKColor(255, 255, 255, 200) };
                float w = font.MeasureText(camera.Caption) + 14;
                canvas.DrawRoundRect(new SKRect(8, 8, 8 + w, 32), 6, 6, back);
                canvas.DrawText(camera.Caption, 15, 26, SKTextAlign.Left, font, ink);
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>The four standard views of a model whose front faces −Z, framed to fit its bounds.</summary>
        public static List<(string View, byte[] Png)> RenderStandardViews(IReadOnlyList<RenderPrimitive> parts, int size = 512)
        {
            var (min, max) = Bounds(parts);
            var center = (min + max) * 0.5f;
            var extent = max - min;
            float radius = MathF.Max(1f, extent.Magnitude * 0.5f);
            float ortho = size * 0.78f / MathF.Max(1f, MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)));
            float step = GridStepFor(MathF.Max(extent.X, extent.Z));
            var arrowOrigin = new RbxVector3(center.X, min.Y, min.Z);
            RenderOptions Opt(bool arrow) => new() { Width = size, Height = size, GroundY = min.Y, GridStep = step, FrontArrow = arrow, FrontArrowOrigin = arrowOrigin };

            string dims = $"{extent.X:0.#} × {extent.Y:0.#} × {extent.Z:0.#} studs (W×H×D) · grid {step:0.#} studs";
            return new List<(string, byte[])>
            {
                ("front", RenderPng(parts, new RenderCamera { Eye = center + new RbxVector3(0, 0, -radius * 4), Target = center, FieldOfView = 0, OrthoScale = ortho, Caption = "FRONT (looking at its front, −Z side)" }, Opt(false))),
                ("right", RenderPng(parts, new RenderCamera { Eye = center + new RbxVector3(radius * 4, 0, 0), Target = center, FieldOfView = 0, OrthoScale = ortho, Caption = "RIGHT side (+X)" }, Opt(true))),
                ("top", RenderPng(parts, new RenderCamera { Eye = center + new RbxVector3(0, radius * 4, 0), Target = center, Up = new RbxVector3(0, 0, -1), FieldOfView = 0, OrthoScale = ortho, Caption = "TOP (front at the top edge)" }, Opt(true))),
                ("iso", RenderPng(parts, new RenderCamera { Eye = center + new RbxVector3(radius * 1.9f, radius * 1.5f, -radius * 2.3f), Target = center, FieldOfView = 38, Caption = "3/4 view · " + dims }, Opt(true))),
            };
        }

        /// <summary>The standard views in sheet order: top-left, top-right, bottom-left, bottom-right.</summary>
        public static readonly string[] SheetOrder = { "front", "right", "top", "iso" };

        /// <summary>
        /// The standard views composed into one sheet, two panels per row in <see cref="SheetOrder"/>, each
        /// scaled to <paramref name="panel"/> pixels. A vision model charges for every image and every
        /// pixel, and flat schematic views lose nothing a labeller needs at this size, so four 512-pixel
        /// pictures become one 768-pixel picture. Null when there is no view to show.
        /// </summary>
        public static byte[]? ComposeSheet(IReadOnlyList<(string View, byte[] Png)> views, int panel = 384)
        {
            var ordered = SheetOrder
                .Select(v => views.FirstOrDefault(x => x.View == v).Png)
                .Where(png => png is { Length: > 0 })
                .ToList();
            if (ordered.Count == 0) return null;
            int columns = Math.Min(2, ordered.Count), rows = (ordered.Count + 1) / 2;
            using var surface = SKSurface.Create(new SKImageInfo(columns * panel, rows * panel, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            for (int i = 0; i < ordered.Count; i++)
            {
                using var bitmap = SKBitmap.Decode(ordered[i]);
                if (bitmap == null) continue;
                using var image = SKImage.FromBitmap(bitmap);
                float x = i % 2 * panel, y = i / 2 * panel;
                canvas.DrawImage(image, new SKRect(x, y, x + panel, y + panel), sampling);
            }
            using (var rule = new SKPaint { Color = new SKColor(60, 66, 78), StrokeWidth = 2 })
            {
                if (columns > 1) canvas.DrawLine(panel, 0, panel, rows * panel, rule);
                for (int r = 1; r < rows; r++) canvas.DrawLine(0, r * panel, columns * panel, r * panel, rule);
            }
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        public static (RbxVector3 Min, RbxVector3 Max) Bounds(IReadOnlyList<RenderPrimitive> parts)
        {
            if (parts.Count == 0) return (new RbxVector3(-1, -1, -1), new RbxVector3(1, 1, 1));
            var min = new RbxVector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new RbxVector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in parts)
                foreach (var c in AssetAnalyzer.Corners(p.CFrame, p.Size))
                {
                    min = min.Min(c);
                    max = max.Max(c);
                }
            return (min, max);
        }

        public static float GridStepFor(float span) => span <= 12 ? 1 : span <= 40 ? 4 : span <= 160 ? 16 : span <= 640 ? 64 : 256;

        /// <summary>Collects the drawable parts of a model (anything with a size and a frame).</summary>
        public static List<RenderPrimitive> PrimitivesOf(RbxInstance root, RbxCFrame? transform = null, float scale = 1)
        {
            var list = new List<RenderPrimitive>();
            foreach (var inst in root.SelfAndDescendants())
            {
                if (!AssetAnalyzer.PartClasses.Contains(inst.ClassName) || inst.Size is not { } size || inst.CFrame is not { } cf) continue;
                if (inst.Transparency >= 0.98f) continue;
                string shape = inst.ClassName switch
                {
                    "WedgePart" => "Wedge",
                    "CornerWedgePart" => "CornerWedge",
                    "TrussPart" => "Block",
                    _ => RbxPartShapes.Name(inst.GetAs<uint>("shape") ?? RbxPartShapes.Block),
                };
                var mesh = inst.FindFirstChildOfClass("SpecialMesh");
                if (mesh != null && mesh.GetAs<uint>("MeshType") is uint meshType)
                    shape = meshType switch { 3 => "Ball", 4 => "Cylinder", 1 => "Wedge", _ => shape }; // Sphere, Cylinder, Wedge
                bool isMesh = inst.ClassName is "MeshPart" or "UnionOperation" || mesh != null && mesh.GetAs<uint>("MeshType") == 5; // FileMesh
                var color = inst.Color ?? new RbxColor3uint8(163, 162, 165);
                var world = transform is { } t ? t * cf.WithPosition(cf.Position * scale) : cf;
                list.Add(new RenderPrimitive(world, size * scale, shape, color, inst.Transparency, isMesh));
            }
            return list;
        }

        // ─────────────── projection ───────────────

        private sealed class View
        {
            private readonly RbxVector3 eye, right, up, forward;
            private readonly float focal, ortho, cx, cy;
            public bool Orthographic { get; }

            public View(RenderCamera camera, int width, int height)
            {
                eye = camera.Eye;
                forward = (camera.Target - camera.Eye).Unit;
                right = forward.Cross(camera.Up).Unit;
                if (right.Magnitude < 1e-6f) right = forward.Cross(new RbxVector3(0, 0, 1)).Unit;
                up = right.Cross(forward);
                cx = width / 2f;
                cy = height / 2f;
                Orthographic = camera.FieldOfView <= 0;
                ortho = camera.OrthoScale;
                focal = Orthographic ? 1 : height / 2f / MathF.Tan(camera.FieldOfView * MathF.PI / 360f);
            }

            public RbxVector3 Eye => eye;
            public RbxVector3 Forward => forward;

            /// <summary>Closest distance in front of a perspective camera that is still drawn.</summary>
            public const float Near = 0.1f;

            public RbxVector3 ToCamera(RbxVector3 world)
            {
                var d = world - eye;
                return new RbxVector3(d.Dot(right), d.Dot(up), d.Dot(forward));
            }

            /// <summary>Screen position of a camera-space point (perspective points must be in front of the near plane).</summary>
            public SKPoint Screen(RbxVector3 c) => Orthographic
                ? new SKPoint(cx + c.X * ortho, cy - c.Y * ortho)
                : new SKPoint(cx + focal * c.X / c.Z, cy - focal * c.Y / c.Z);

            /// <summary>Nearness, larger is closer. 1/z (perspective) and −z (orthographic) are both linear across the screen.</summary>
            public float Nearness(RbxVector3 c) => Orthographic ? -c.Z : 1f / c.Z;

            public (SKPoint Point, float Depth) Project(RbxVector3 world)
            {
                var c = ToCamera(world);
                if (Orthographic) return (Screen(c), c.Z);
                return (Screen(new RbxVector3(c.X, c.Y, MathF.Max(c.Z, 0.05f))), c.Z);
            }

            /// <summary>Cuts away the part of a camera-space polygon behind the near plane (perspective only).</summary>
            public RbxVector3[] ClipNear(RbxVector3[] poly)
            {
                if (Orthographic || poly.All(v => v.Z >= Near)) return poly;
                var kept = new List<RbxVector3>(poly.Length + 2);
                for (int i = 0; i < poly.Length; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Length];
                    bool aIn = a.Z >= Near, bIn = b.Z >= Near;
                    if (aIn) kept.Add(a);
                    if (aIn != bIn) kept.Add(a + (b - a) * ((Near - a.Z) / (b.Z - a.Z)));
                }
                return kept.ToArray();
            }
        }

        /// <summary>
        /// A supersampled colour + depth buffer. Faces are filled sample by sample with a depth test, their
        /// edges darkened from the inside, and the result averaged down to the output size (anti-aliased).
        /// Colours are premultiplied so the finished layer composites over the sky and grid.
        /// </summary>
        private sealed class Raster
        {
            private readonly int width, height, scale, bw, bh;
            private readonly float[] near;
            private readonly byte[] rgba;

            public Raster(int width, int height)
            {
                this.width = width;
                this.height = height;
                scale = (long)width * height <= 420_000 ? 3 : 2;
                bw = width * scale;
                bh = height * scale;
                near = new float[bw * bh];
                Array.Fill(near, float.NegativeInfinity);
                rgba = new byte[bw * bh * 4];
            }

            public void Fill(View view, Face face, bool depthWrite)
            {
                int n = face.Camera.Length;
                if (n < 3) return;
                var xs = new float[n];
                var ys = new float[n];
                var ks = new float[n];
                for (int i = 0; i < n; i++)
                {
                    var p = view.Screen(face.Camera[i]);
                    xs[i] = p.X * scale;
                    ys[i] = p.Y * scale;
                    ks[i] = view.Nearness(face.Camera[i]);
                }
                double area = 0;
                for (int i = 0; i < n; i++) { int j = (i + 1) % n; area += (double)xs[i] * ys[j] - (double)xs[j] * ys[i]; }
                if (Math.Abs(area) < 1e-4) return; // edge-on
                float sign = area > 0 ? 1 : -1;

                // Nearness is a plane over the screen: k = ka·x + kb·y + kc, fitted to the widest triangle.
                int i1 = 1, i2 = 2;
                double best = 0;
                for (int a = 1; a < n - 1; a++)
                    for (int b = a + 1; b < n; b++)
                    {
                        double t = Math.Abs((xs[a] - xs[0]) * (ys[b] - ys[0]) - (xs[b] - xs[0]) * (ys[a] - ys[0]));
                        if (t > best) { best = t; i1 = a; i2 = b; }
                    }
                double det = (xs[i1] - xs[0]) * (ys[i2] - ys[0]) - (xs[i2] - xs[0]) * (ys[i1] - ys[0]);
                if (Math.Abs(det) < 1e-9) return;
                double ka = ((ks[i1] - ks[0]) * (ys[i2] - ys[0]) - (ks[i2] - ks[0]) * (ys[i1] - ys[0])) / det;
                double kb = ((xs[i1] - xs[0]) * (ks[i2] - ks[0]) - (xs[i2] - xs[0]) * (ks[i1] - ks[0])) / det;
                double kc = ks[0] - ka * xs[0] - kb * ys[0];

                // Inward edge normals, unit length: their value at a sample is its distance inside that edge.
                var ex = new float[n]; var ey = new float[n]; var ec = new float[n]; var len = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float dx = xs[j] - xs[i], dy = ys[j] - ys[i];
                    float l = MathF.Sqrt(dx * dx + dy * dy);
                    len[i] = l;
                    if (l < 1e-6f) { ex[i] = ey[i] = 0; ec[i] = float.MaxValue; continue; }
                    ex[i] = -sign * dy / l;
                    ey[i] = sign * dx / l;
                    ec[i] = -(ex[i] * xs[i] + ey[i] * ys[i]);
                }

                float stroke = (face.Outline ? 1.2f : 0.7f) * scale;
                var edge = face.Outline ? new SKColor(30, 30, 30, 170) : new SKColor(0, 0, 0, 60);
                var fill = face.Fill;
                var lined = Blend(fill, edge);
                int x0 = Math.Max(0, (int)MathF.Floor(xs.Min())), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(xs.Max()));
                int y0 = Math.Max(0, (int)MathF.Floor(ys.Min())), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(ys.Max()));
                for (int y = y0; y <= y1; y++)
                {
                    float sy = y + 0.5f;
                    for (int x = x0; x <= x1; x++)
                    {
                        float sx = x + 0.5f;
                        float inside = float.MaxValue;
                        int nearestEdge = 0;
                        bool outside = false;
                        for (int e = 0; e < n; e++)
                        {
                            float d = ex[e] * sx + ey[e] * sy + ec[e];
                            if (d < 0) { outside = true; break; }
                            if (d < inside) { inside = d; nearestEdge = e; }
                        }
                        if (outside) continue;
                        int idx = y * bw + x;
                        float k = (float)(ka * sx + kb * sy + kc);
                        if (k <= near[idx]) continue;
                        var colour = fill;
                        if (inside < stroke)
                        {
                            bool on = true;
                            if (face.Outline)
                            {
                                // Dashed (5 on, 3 off, in output pixels) along the nearest edge.
                                int e = nearestEdge, j = (e + 1) % n;
                                float along = len[e] < 1e-6f ? 0 : ((sx - xs[e]) * (xs[j] - xs[e]) + (sy - ys[e]) * (ys[j] - ys[e])) / len[e] / scale;
                                on = ((along % 8f) + 8f) % 8f < 5f;
                            }
                            if (on) colour = lined;
                        }
                        Put(idx, colour);
                        if (depthWrite) near[idx] = k;
                    }
                }
            }

            private static SKColor Blend(SKColor under, SKColor over)
            {
                float a = over.Alpha / 255f;
                return new SKColor((byte)(under.Red * (1 - a) + over.Red * a), (byte)(under.Green * (1 - a) + over.Green * a), (byte)(under.Blue * (1 - a) + over.Blue * a), under.Alpha);
            }

            /// <summary>Source-over in premultiplied form (an opaque colour simply replaces what was there).</summary>
            private void Put(int idx, SKColor c)
            {
                int o = idx * 4;
                if (c.Alpha == 255)
                {
                    rgba[o] = c.Red; rgba[o + 1] = c.Green; rgba[o + 2] = c.Blue; rgba[o + 3] = 255;
                    return;
                }
                float a = c.Alpha / 255f, keep = 1 - a;
                rgba[o] = (byte)Math.Min(255, c.Red * a + rgba[o] * keep + 0.5f);
                rgba[o + 1] = (byte)Math.Min(255, c.Green * a + rgba[o + 1] * keep + 0.5f);
                rgba[o + 2] = (byte)Math.Min(255, c.Blue * a + rgba[o + 2] * keep + 0.5f);
                rgba[o + 3] = (byte)Math.Min(255, c.Alpha + rgba[o + 3] * keep + 0.5f);
            }

            /// <summary>Averages each scale×scale block into one output pixel (premultiplied RGBA).</summary>
            public SKBitmap Resolve()
            {
                var output = new byte[width * height * 4];
                int samples = scale * scale;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int r = 0, g = 0, b = 0, a = 0;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            int row = ((y * scale + sy) * bw + x * scale) * 4;
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int o = row + sx * 4;
                                r += rgba[o]; g += rgba[o + 1]; b += rgba[o + 2]; a += rgba[o + 3];
                            }
                        }
                        int p = (y * width + x) * 4;
                        output[p] = (byte)(r / samples);
                        output[p + 1] = (byte)(g / samples);
                        output[p + 2] = (byte)(b / samples);
                        output[p + 3] = (byte)(a / samples);
                    }
                var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                IntPtr pixels = bitmap.GetPixels();
                for (int y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(output, y * width * 4, pixels + y * bitmap.RowBytes, width * 4);
                return bitmap;
            }
        }

        private static void AddFaces(List<Face> faces, RenderPrimitive p, View view, bool labels)
        {
            var h = p.Size * 0.5f;
            var polys = new List<RbxVector3[]>();
            switch (p.Shape)
            {
                case "Ball":
                    polys.AddRange(SpherePolys(h));
                    break;
                case "Cylinder":
                    polys.AddRange(CylinderPolys(h));
                    break;
                case "Wedge":
                    {
                        // Roblox wedge: full height at the back (+Z), sloping down to the front (−Z).
                        var b0 = new RbxVector3(-h.X, -h.Y, -h.Z); var b1 = new RbxVector3(h.X, -h.Y, -h.Z);
                        var b2 = new RbxVector3(h.X, -h.Y, h.Z); var b3 = new RbxVector3(-h.X, -h.Y, h.Z);
                        var t2 = new RbxVector3(h.X, h.Y, h.Z); var t3 = new RbxVector3(-h.X, h.Y, h.Z);
                        polys.Add(new[] { b0, b3, b2, b1 }); // bottom
                        polys.Add(new[] { b3, t3, t2, b2 }); // back
                        polys.Add(new[] { b0, b1, t2, t3 }); // slope
                        polys.Add(new[] { b0, t3, b3 });     // left
                        polys.Add(new[] { b1, b2, t2 });     // right
                        break;
                    }
                case "CornerWedge":
                    {
                        var b0 = new RbxVector3(-h.X, -h.Y, -h.Z); var b1 = new RbxVector3(h.X, -h.Y, -h.Z);
                        var b2 = new RbxVector3(h.X, -h.Y, h.Z); var b3 = new RbxVector3(-h.X, -h.Y, h.Z);
                        var top = new RbxVector3(h.X, h.Y, -h.Z);
                        polys.Add(new[] { b0, b3, b2, b1 });
                        polys.Add(new[] { b1, b2, top });
                        polys.Add(new[] { b0, b1, top });
                        polys.Add(new[] { b2, b3, top });
                        polys.Add(new[] { b3, b0, top });
                        break;
                    }
                default:
                    polys.AddRange(BoxPolys(h));
                    break;
            }

            // Every solid here is convex, so a face is outward-wound when its normal points away from the
            // solid's centroid; orienting by that test means no polygon list has to get its winding right.
            var vertices = polys.SelectMany(x => x).ToList();
            var centroid = vertices.Aggregate(RbxVector3.Zero, (a, b) => a + b) / Math.Max(1, vertices.Count);
            for (int i = 0; i < polys.Count; i++)
            {
                var poly = polys[i];
                var n = (poly[1] - poly[0]).Cross(poly[2] - poly[0]);
                var mid = poly.Aggregate(RbxVector3.Zero, (a, b) => a + b) / poly.Length;
                if (n.Dot(mid - centroid) < 0) polys[i] = poly.Reverse().ToArray();
            }

            bool cull = !p.IsMesh && p.Transparency < 0.05f;
            byte alpha = (byte)Math.Clamp((int)((1 - p.Transparency) * (p.IsMesh ? 225 : 255)), 25, 255);
            bool labelled = false;
            foreach (var local in polys)
            {
                var world = local.Select(v => p.CFrame.PointToWorldSpace(v)).ToArray();
                var normal = (world[1] - world[0]).Cross(world[2] - world[0]).Unit;
                var centre = world.Aggregate(RbxVector3.Zero, (a, b) => a + b) / world.Length;
                var toEye = view.Orthographic ? -view.Forward : (view.Eye - centre).Unit;
                if (cull && normal.Dot(toEye) <= 0) continue;
                float light = 0.48f + 0.52f * MathF.Max(0, normal.Dot(LightDirection));
                // Faces seen from behind (transparent or mesh boxes) shade by their outward side anyway.
                var c = p.Color;
                var fill = new SKColor((byte)Math.Min(255, c.R * light), (byte)Math.Min(255, c.G * light), (byte)Math.Min(255, c.B * light), alpha);
                var camera = view.ClipNear(world.Select(view.ToCamera).ToArray());
                if (camera.Length < 3) continue; // wholly behind the camera
                float depth = camera.Average(v => v.Z);
                var (labelAt, _) = view.Project(centre);
                faces.Add(new Face(camera, depth, fill, p.IsMesh, labels && !labelled ? p.Label : null, labelAt));
                labelled = true;
            }
        }

        private static IEnumerable<RbxVector3[]> BoxPolys(RbxVector3 h)
        {
            var v = new RbxVector3[8];
            for (int i = 0; i < 8; i++) v[i] = new RbxVector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            // Counter-clockwise seen from outside, so the cross product points outward.
            yield return new[] { v[0], v[4], v[6], v[2] }; // -X
            yield return new[] { v[1], v[3], v[7], v[5] }; // +X
            yield return new[] { v[0], v[1], v[5], v[4] }; // -Y
            yield return new[] { v[2], v[6], v[7], v[3] }; // +Y
            yield return new[] { v[0], v[2], v[3], v[1] }; // -Z
            yield return new[] { v[4], v[5], v[7], v[6] }; // +Z
        }

        private static IEnumerable<RbxVector3[]> CylinderPolys(RbxVector3 h)
        {
            // Roblox cylinders run along the part's X axis.
            const int sides = 14;
            float r = MathF.Min(h.Y, h.Z);
            var left = new RbxVector3[sides];
            var right = new RbxVector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * MathF.Tau / sides;
                float y = MathF.Cos(a) * r, z = MathF.Sin(a) * r;
                left[i] = new RbxVector3(-h.X, y, z);
                right[i] = new RbxVector3(h.X, y, z);
            }
            yield return left.Reverse().ToArray();
            yield return right.ToArray();
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                yield return new[] { left[i], left[j], right[j], right[i] };
            }
        }

        private static IEnumerable<RbxVector3[]> SpherePolys(RbxVector3 h)
        {
            const int rings = 7, segments = 12;
            float rx = h.X, ry = h.Y, rz = h.Z;
            RbxVector3 P(int ring, int seg)
            {
                float phi = MathF.PI * ring / rings;
                float theta = MathF.Tau * seg / segments;
                return new RbxVector3(rx * MathF.Sin(phi) * MathF.Cos(theta), ry * MathF.Cos(phi), rz * MathF.Sin(phi) * MathF.Sin(theta));
            }
            for (int ring = 0; ring < rings; ring++)
                for (int seg = 0; seg < segments; seg++)
                {
                    var a = P(ring, seg); var b = P(ring + 1, seg); var c = P(ring + 1, seg + 1); var d = P(ring, seg + 1);
                    if (ring == 0) yield return new[] { a, b, c };
                    else if (ring == rings - 1) yield return new[] { a, b, d };
                    else yield return new[] { a, b, c, d };
                }
        }

        private static void DrawGrid(SKCanvas canvas, View view, IReadOnlyList<RenderPrimitive> parts, RenderOptions options)
        {
            var (min, max) = Bounds(parts);
            float span = MathF.Max(max.X - min.X, max.Z - min.Z);
            float step = options.GridStep > 0 ? options.GridStep : GridStepFor(span);
            float half = MathF.Ceiling((span * 0.75f + step * 2) / step) * step;
            float cx = MathF.Round((min.X + max.X) / 2 / step) * step, cz = MathF.Round((min.Z + max.Z) / 2 / step) * step;
            using var minor = new SKPaint { Color = new SKColor(120, 130, 145, 70), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
            for (float x = cx - half; x <= cx + half + 0.01f; x += step)
            {
                var (a, da) = view.Project(new RbxVector3(x, options.GroundY, cz - half));
                var (b, db) = view.Project(new RbxVector3(x, options.GroundY, cz + half));
                if (da > 0 || view.Orthographic) canvas.DrawLine(a, b, minor);
            }
            for (float z = cz - half; z <= cz + half + 0.01f; z += step)
            {
                var (a, da) = view.Project(new RbxVector3(cx - half, options.GroundY, z));
                var (b, db) = view.Project(new RbxVector3(cx + half, options.GroundY, z));
                if (da > 0 || view.Orthographic) canvas.DrawLine(a, b, minor);
            }
        }

        private static void DrawFrontArrow(SKCanvas canvas, View view, RenderOptions options, IReadOnlyList<RenderPrimitive> parts)
        {
            var (min, max) = Bounds(parts);
            float len = MathF.Max(2f, (max.Z - min.Z) * 0.35f + 2f);
            var start = options.FrontArrowOrigin;
            var end = start + new RbxVector3(0, 0, -len);
            var (a, _) = view.Project(start);
            var (b, _) = view.Project(end);
            using var paint = new SKPaint { Color = new SKColor(214, 48, 49), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
            canvas.DrawLine(a, b, paint);
            var dir = new SKPoint(b.X - a.X, b.Y - a.Y);
            float l = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (l > 4)
            {
                var u = new SKPoint(dir.X / l, dir.Y / l);
                var n = new SKPoint(-u.Y, u.X);
                canvas.DrawLine(b, new SKPoint(b.X - u.X * 10 + n.X * 6, b.Y - u.Y * 10 + n.Y * 6), paint);
                canvas.DrawLine(b, new SKPoint(b.X - u.X * 10 - n.X * 6, b.Y - u.Y * 10 - n.Y * 6), paint);
            }
            using var font = new SKFont(SKTypeface.Default, 13);
            using var ink = new SKPaint { Color = new SKColor(214, 48, 49), IsAntialias = true };
            canvas.DrawText("FRONT", b.X + 6, b.Y - 4, SKTextAlign.Left, font, ink);
        }
    }
}
