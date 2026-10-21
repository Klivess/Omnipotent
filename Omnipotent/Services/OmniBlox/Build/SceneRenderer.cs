using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>
    /// Pictures of a compiled world for the agents, the dashboard and the marketing. A thin front for
    /// <see cref="WorldRenderer"/>: callers with the studio at hand use StudioContext.Render, which adds the baked
    /// terrain and Roblox's pictures of the placed models; this overload draws what it is given.
    /// </summary>
    public static class SceneRenderer
    {
        /// <summary>overview, map, iso, aerial, spawn, zone:&lt;id&gt;, an object id or "x,y,z>x,y,z".</summary>
        public static byte[] Render(IReadOnlyList<WorldPart> parts, SceneDocument scene, string view, int width = 960, int height = 600, bool labels = false, bool caption = true,
            TerrainMap? terrain = null, Func<long, SpriteImage?>? sprites = null, bool marketing = false)
        {
            if (terrain == null && scene.Land != null) terrain = TerrainBuilder.Build(scene.Land);
            var world = new RenderWorld { Parts = parts, Scene = scene, Terrain = terrain, Sprite = sprites ?? (_ => null) };
            return WorldRenderer.Render(world, new RenderRequest { View = view, Width = width, Height = height, Labels = labels, Caption = caption && !marketing, Marketing = marketing });
        }

        /// <summary>The old flat-shaded primitives (the trailer's frames and the asset sheets still draw from these).</summary>
        public static List<RenderPrimitive> Primitives(IReadOnlyList<WorldPart> parts, SceneDocument scene, bool labels = false)
        {
            var list = new List<RenderPrimitive>();
            foreach (var t in scene.Terrain)
            {
                var size = new RbxVector3(t.Size[0], t.Size[1], t.Size[2]);
                list.Add(new RenderPrimitive(new RbxCFrame(new RbxVector3(t.Center[0], t.Center[1], t.Center[2])), size,
                    t.Shape == "ball" ? "Ball" : "Block", new RbxColor3uint8(98, 171, 72), t.Material.Equals("Water", StringComparison.OrdinalIgnoreCase) ? 0.25f : 0f, false));
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
    }
}
