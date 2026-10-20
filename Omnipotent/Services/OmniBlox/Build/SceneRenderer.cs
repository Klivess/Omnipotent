using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>Renders a compiled world (parts plus terrain fills) from useful cameras: overview, 3/4, spawn eye-level, or around one object.</summary>
    public static class SceneRenderer
    {
        private static readonly Dictionary<string, RbxColor3uint8> TerrainColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Grass"] = new(106, 160, 72), ["LeafyGrass"] = new(96, 150, 64), ["Sand"] = new(222, 205, 150), ["Water"] = new(70, 140, 205),
            ["Rock"] = new(120, 118, 115), ["Snow"] = new(240, 244, 248), ["Ground"] = new(130, 105, 80), ["Mud"] = new(95, 75, 55),
            ["Slate"] = new(90, 95, 100), ["Ice"] = new(190, 220, 240), ["Basalt"] = new(60, 60, 64), ["Asphalt"] = new(70, 70, 74),
            ["Sandstone"] = new(205, 160, 110), ["Glacier"] = new(170, 210, 235), ["CrackedLava"] = new(160, 70, 40),
        };

        public static List<RenderPrimitive> Primitives(IReadOnlyList<WorldPart> parts, SceneDocument scene, bool labels = false)
        {
            var list = new List<RenderPrimitive>();
            foreach (var t in scene.Terrain)
            {
                var size = new RbxVector3(t.Size[0], t.Size[1], t.Size[2]);
                var color = TerrainColors.TryGetValue(t.Material, out var c) ? c : new RbxColor3uint8(110, 150, 80);
                list.Add(new RenderPrimitive(new RbxCFrame(new RbxVector3(t.Center[0], t.Center[1], t.Center[2])), size,
                    t.Shape == "ball" ? "Ball" : "Block", color, t.Material.Equals("Water", StringComparison.OrdinalIgnoreCase) ? 0.25f : 0f, false));
            }
            foreach (var p in parts.Where(p => p.Transparency < 0.98f))
                list.Add(new RenderPrimitive(p.CFrame, p.Size, p.Shape, p.Color, p.Transparency, p.IsMesh, labels ? p.ObjectId : null));
            return list;
        }

        public static (RbxVector3 Min, RbxVector3 Max) PlayBounds(IReadOnlyList<WorldPart> parts)
        {
            var solid = parts.Where(p => p.Size.X < 400 && p.Size.Z < 400).ToList(); // big base plates would swamp the framing
            var source = solid.Count > 0 ? solid : parts.ToList();
            return SchematicRenderer.Bounds(source.Select(p => new RenderPrimitive(p.CFrame, p.Size, p.Shape, p.Color, 0, false)).ToList());
        }

        /// <summary>overview (straight down), iso (3/4 from above), spawn (eye level at the spawn looking ahead) or an object id.</summary>
        public static byte[] Render(IReadOnlyList<WorldPart> parts, SceneDocument scene, string view, int width = 960, int height = 600, bool labels = false, bool caption = true)
        {
            var prims = Primitives(parts, scene, labels);
            var (min, max) = PlayBounds(parts);
            var center = (min + max) * 0.5f;
            var extent = max - min;
            float radius = MathF.Max(8, extent.Magnitude * 0.5f);
            RenderCamera camera;
            switch (view.ToLowerInvariant())
            {
                case "overview":
                    {
                        float span = MathF.Max(extent.X, extent.Z) * 1.15f + 8;
                        camera = new RenderCamera { Eye = center + new RbxVector3(0, radius * 6, 0), Target = center, Up = new RbxVector3(0, 0, -1), FieldOfView = 0, OrthoScale = MathF.Min(width, height) / span, Caption = $"Overview from above (−Z is up the image) · {extent.X:0}×{extent.Z:0} studs" };
                        break;
                    }
                case "spawn":
                    {
                        var spawn = parts.FirstOrDefault(p => p.Kind == SceneKinds.Spawn) ?? parts[0];
                        var eye = spawn.CFrame.Position + new RbxVector3(0, spawn.Size.Y / 2 + 5, 0);
                        var forward = MathF.Abs(center.X - eye.X) + MathF.Abs(center.Z - eye.Z) > 4 ? new RbxVector3(center.X, eye.Y - 2, center.Z) : eye + new RbxVector3(0, -2, -20);
                        camera = new RenderCamera { Eye = eye + (eye - forward).Unit * 6, Target = forward, FieldOfView = 70, Caption = "What a player sees on spawning" };
                        break;
                    }
                case "iso":
                case "":
                    camera = new RenderCamera { Eye = center + new RbxVector3(radius * 1.2f, radius * 1.1f, radius * 1.4f), Target = center, FieldOfView = 45, Caption = "3/4 view" };
                    break;
                default:
                    {
                        var focus = parts.Where(p => p.ObjectId.Equals(view, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (focus.Count == 0) throw new ArgumentException($"no object '{view}' in the scene (use overview, iso, spawn or an object id)");
                        var (fmin, fmax) = SchematicRenderer.Bounds(focus.Select(p => new RenderPrimitive(p.CFrame, p.Size, p.Shape, p.Color, 0, false)).ToList());
                        var fc = (fmin + fmax) * 0.5f;
                        float fr = MathF.Max(4, (fmax - fmin).Magnitude * 0.5f);
                        camera = new RenderCamera { Eye = fc + new RbxVector3(fr * 1.6f, fr * 1.2f, -fr * 2.0f), Target = fc, FieldOfView = 45, Caption = $"Close-up of {view}" };
                        break;
                    }
            }
            if (!caption)
                camera = new RenderCamera { Eye = camera.Eye, Target = camera.Target, Up = camera.Up, FieldOfView = camera.FieldOfView, OrthoScale = camera.OrthoScale };
            float step = SchematicRenderer.GridStepFor(MathF.Max(extent.X, extent.Z));
            return SchematicRenderer.RenderPng(prims, camera, new RenderOptions { Width = width, Height = height, Grid = false, GridStep = step, GroundY = min.Y, Sky = true, Labels = labels });
        }
    }
}
