using Newtonsoft.Json;
using System.Globalization;

namespace Omnipotent.Services.Stratum
{
    /// <summary>One part staged for composition/collision/render: a STEP file + its blueprint slot.</summary>
    public class StratumCompositionEntry
    {
        public string StagedFileName { get; set; } = "";
        public string SubtaskTitle { get; set; } = "";
        public MechanicalBlueprintSlot? Slot { get; set; }
    }

    /// <summary>One pairwise collision/clearance measurement from a STRATUM_COLLISION line.</summary>
    public class StratumCollisionPair
    {
        [JsonProperty("a")] public string A { get; set; } = "";
        [JsonProperty("b")] public string B { get; set; } = "";
        [JsonProperty("aTask")] public string ATask { get; set; } = "";
        [JsonProperty("bTask")] public string BTask { get; set; } = "";
        [JsonProperty("method")] public string Method { get; set; } = "occ";
        [JsonProperty("intersectionMm3")] public double IntersectionMm3 { get; set; }
        [JsonProperty("minClearanceMm")] public double MinClearanceMm { get; set; }
        [JsonProperty("error")] public string? Error { get; set; }
    }

    /// <summary>
    /// Generators + parsers for the deterministic Python geometry operations Stratum runs via
    /// <see cref="StratumPythonRunner"/>: assembly composition, exact collision/clearance
    /// checking, multi-view rendering (matplotlib Agg — headless-safe on Windows), and
    /// measurement queries. All scripts are pure CadQuery/OCP/matplotlib/numpy.
    /// </summary>
    public static class StratumGeometryOps
    {
        /// <summary>Default render palette (cycled per part).</summary>
        private static readonly string[] Palette =
            { "#4e79a7", "#f28e2b", "#59a14f", "#e15759", "#b07aa1", "#76b7b2", "#edc948", "#ff9da7" };

        // ───────────────────────── shared placement emission ─────────────────────────

        private static string SafeName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Replace(' ', '_');
            return clean.Length > 80 ? clean.Substring(0, 80) : clean;
        }

        private static List<double[]> InstancePoses(MechanicalBlueprintSlot? slot)
        {
            if (slot?.Instances != null && slot.Instances.Count > 0) return slot.Instances;
            double px = 0, py = 0, pz = 0, rx = 0, ry = 0, rz = 0;
            if (slot?.WorldPosition is { Length: >= 3 } wp) { px = wp[0]; py = wp[1]; pz = wp[2]; }
            if (slot?.WorldRotationDeg is { Length: >= 3 } wr) { rx = wr[0]; ry = wr[1]; rz = wr[2]; }
            return new List<double[]> { new[] { px, py, pz, rx, ry, rz } };
        }

        // Emits python that imports a STEP, rotates (Euler XYZ on geometry) and translates it,
        // and appends ('name', 'task', placedWorkplane) tuples to _placed.
        private static void EmitPlacements(System.Text.StringBuilder sb, StratumCompositionEntry entry, int idx)
        {
            string safe = SafeName(entry.SubtaskTitle);
            sb.AppendLine($"shape_{idx} = cq.importers.importStep(r'{entry.StagedFileName}')");
            int instIdx = 0;
            foreach (var inst in InstancePoses(entry.Slot))
            {
                instIdx++;
                double ix = G(inst, 0), iy = G(inst, 1), iz = G(inst, 2);
                double irx = G(inst, 3), iry = G(inst, 4), irz = G(inst, 5);
                string v = $"p_{idx}_{instIdx}";
                sb.AppendLine($"{v} = shape_{idx}");
                if (Math.Abs(irx) > 1e-9) sb.AppendLine($"{v} = {v}.rotate((0, 0, 0), (1, 0, 0), {N(irx)})");
                if (Math.Abs(iry) > 1e-9) sb.AppendLine($"{v} = {v}.rotate((0, 0, 0), (0, 1, 0), {N(iry)})");
                if (Math.Abs(irz) > 1e-9) sb.AppendLine($"{v} = {v}.rotate((0, 0, 0), (0, 0, 1), {N(irz)})");
                sb.AppendLine($"_placed.append(('{safe}_{instIdx}', {PyStr(entry.SubtaskTitle)}, {v}, ({N(ix)}, {N(iy)}, {N(iz)})))");
            }
        }

        private const string PlacementPrologue = @"import cadquery as cq
_placed = []  # (name, task, workplane_local, (tx, ty, tz))

def _to_world_compound(wp, t):
    c = cq.Compound.makeCompound(wp.vals()) if hasattr(wp, 'vals') else wp
    return c.translate(cq.Vector(*t))

def _occ_vol(w):
    from OCP.GProp import GProp_GProps
    from OCP.BRepGProp import BRepGProp
    p = GProp_GProps()
    BRepGProp.VolumeProperties_s(w, p)
    return abs(p.Mass())

def _occ_common_vol(aw, bw):
    from OCP.BRepAlgoAPI import BRepAlgoAPI_Common
    op = BRepAlgoAPI_Common(aw, bw)
    op.Build()
    if not op.IsDone():
        return None
    return _occ_vol(op.Shape())

def _occ_min_dist(aw, bw):
    from OCP.BRepExtrema import BRepExtrema_DistShapeShape
    d = BRepExtrema_DistShapeShape(aw, bw)
    d.Perform()
    return d.Value() if d.IsDone() else None

def _aabb(c):
    bb = c.BoundingBox()
    return (bb.xmin, bb.xmax, bb.ymin, bb.ymax, bb.zmin, bb.zmax)

def _aabb_dist(a, b):
    import math
    gx = max(a[0] - b[1], b[0] - a[1], 0.0)
    gy = max(a[2] - b[3], b[2] - a[3], 0.0)
    gz = max(a[4] - b[5], b[4] - a[5], 0.0)
    return math.sqrt(gx * gx + gy * gy + gz * gz)
";

        private const string CollisionCheckSnippet = @"
import json as _json
_world = []  # index-aligned with _placed; None when placement failed
for (_name, _task, _wp, _t) in _placed:
    try:
        _c = _to_world_compound(_wp, _t)
        _world.append((_name, _task, _c, _aabb(_c)))
    except Exception as _e:
        print(f'STRATUM_PLACE_FAILED:{_name}: {_e}')
        _world.append(None)

def _check_pair(a, b):
    if a is None or b is None:
        return None
    rec = {'a': a[0], 'b': b[0], 'aTask': a[1], 'bTask': b[1]}
    try:
        d_aabb = _aabb_dist(a[3], b[3])
        if d_aabb > 2.0:
            rec['method'] = 'aabb'
            rec['intersectionMm3'] = 0.0
            rec['minClearanceMm'] = round(d_aabb, 3)
        else:
            iv = _occ_common_vol(a[2].wrapped, b[2].wrapped)
            md = _occ_min_dist(a[2].wrapped, b[2].wrapped)
            if iv is None or md is None:
                rec['method'] = 'aabb-fallback'
                rec['intersectionMm3'] = 0.0 if d_aabb > 0 else -1.0
                rec['minClearanceMm'] = round(d_aabb, 3)
            else:
                rec['method'] = 'occ'
                rec['intersectionMm3'] = round(iv, 3)
                rec['minClearanceMm'] = round(md, 3)
    except Exception as _e:
        rec['method'] = 'error'
        rec['error'] = str(_e)
    print('STRATUM_COLLISION:' + _json.dumps(rec))
    return rec
";

        // ───────────────────────── composition (moved from StratumMechanicalAgent) ─────────────────────────

        /// <summary>
        /// Deterministically rebuilds the cumulative assembly from approved STEPs using the
        /// blueprint placements, exporting assembly_progress.glb/.step. Emits legacy
        /// STRATUM_PART_BBOX / STRATUM_OVERLAP diagnostics plus exact STRATUM_COLLISION lines.
        /// </summary>
        public static string BuildCompositionScript(
            List<StratumCompositionEntry> entries,
            MechanicalBlueprint blueprint,
            StratumElectronicsLayout? electronicsLayout)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(PlacementPrologue);
            int idx = 0;
            foreach (var e in entries)
            {
                idx++;
                if (e.Slot != null && e.Slot.Virtual) continue;
                EmitPlacements(sb, e, idx);
            }

            sb.AppendLine("asm = cq.Assembly()");
            sb.AppendLine("_part_world_bboxes = []");
            sb.AppendLine("for (_name, _task, _wp, _t) in _placed:");
            sb.AppendLine("    asm.add(_wp, name=_name, loc=cq.Location(cq.Vector(*_t)))");
            sb.AppendLine("    try:");
            sb.AppendLine("        _c = _to_world_compound(_wp, _t)");
            sb.AppendLine("        _bb = _c.BoundingBox()");
            sb.AppendLine("        _part_world_bboxes.append((_name, _bb.xmax-_bb.xmin, _bb.ymax-_bb.ymin, _bb.zmax-_bb.zmin, (_bb.xmax+_bb.xmin)/2, (_bb.ymax+_bb.ymin)/2, (_bb.zmax+_bb.zmin)/2, _bb.xmin, _bb.xmax, _bb.ymin, _bb.ymax, _bb.zmin, _bb.zmax))");
            sb.AppendLine("    except Exception as _e:");
            sb.AppendLine("        print(f'STRATUM_PART_BBOX_FAILED:{_name}: {_e}')");

            // Electronics overlay: translucent labelled box at each module's world pose.
            // Naming prefix `_electronics_` lets the frontend toggle their visibility independently.
            if (electronicsLayout != null && electronicsLayout.Placements.Count > 0)
            {
                int eidx = 0;
                foreach (var p in electronicsLayout.Placements)
                {
                    eidx++;
                    var f = p.Footprint;
                    if (f == null || f.DxMm <= 0 || f.DyMm <= 0 || f.DzMm <= 0) continue;
                    double ex = G(p.WorldPositionMm, 0), ey = G(p.WorldPositionMm, 1), ez = G(p.WorldPositionMm, 2);
                    double erx = G(p.WorldRotationDeg, 0), ery = G(p.WorldRotationDeg, 1), erz = G(p.WorldRotationDeg, 2);
                    string safeInst = SafeName(p.InstanceId);
                    string boxVar = $"_ebox_{eidx}";
                    sb.AppendLine($"{boxVar} = cq.Workplane('XY').box({N(f.DxMm)}, {N(f.DyMm)}, {N(f.DzMm)})");
                    if (Math.Abs(erx) > 1e-9) sb.AppendLine($"{boxVar} = {boxVar}.rotate((0, 0, 0), (1, 0, 0), {N(erx)})");
                    if (Math.Abs(ery) > 1e-9) sb.AppendLine($"{boxVar} = {boxVar}.rotate((0, 0, 0), (0, 1, 0), {N(ery)})");
                    if (Math.Abs(erz) > 1e-9) sb.AppendLine($"{boxVar} = {boxVar}.rotate((0, 0, 0), (0, 0, 1), {N(erz)})");
                    sb.AppendLine($"asm.add({boxVar}, name='_electronics_{safeInst}',");
                    sb.AppendLine($"        loc=cq.Location(cq.Vector({N(ex)}, {N(ey)}, {N(ez)})))");
                }
            }

            sb.AppendLine("result = asm");
            sb.AppendLine();
            sb.AppendLine("# Diagnostics: per-part world bbox (legacy) + exact pairwise collision report.");
            sb.AppendLine("for _row in _part_world_bboxes:");
            sb.AppendLine("    _n,_dx,_dy,_dz,_cx,_cy,_cz,_x0,_x1,_y0,_y1,_z0,_z1 = _row");
            sb.AppendLine("    print(f'STRATUM_PART_BBOX:{_n}:size={_dx:.1f}x{_dy:.1f}x{_dz:.1f} centre=({_cx:.1f},{_cy:.1f},{_cz:.1f})')");
            sb.AppendLine("for _i in range(len(_part_world_bboxes)):");
            sb.AppendLine("    for _j in range(_i+1, len(_part_world_bboxes)):");
            sb.AppendLine("        _a = _part_world_bboxes[_i]; _b = _part_world_bboxes[_j]");
            sb.AppendLine("        _ox = max(0, min(_a[8], _b[8]) - max(_a[7], _b[7]))");
            sb.AppendLine("        _oy = max(0, min(_a[10], _b[10]) - max(_a[9], _b[9]))");
            sb.AppendLine("        _oz = max(0, min(_a[12], _b[12]) - max(_a[11], _b[11]))");
            sb.AppendLine("        if _ox > 0.5 and _oy > 0.5 and _oz > 0.5:");
            sb.AppendLine("            print(f'STRATUM_OVERLAP:{_a[0]} <> {_b[0]}: {_ox:.1f}x{_oy:.1f}x{_oz:.1f} mm')");
            sb.AppendLine(CollisionCheckSnippet);
            sb.AppendLine("for _i in range(len(_world)):");
            sb.AppendLine("    for _j in range(_i+1, len(_world)):");
            sb.AppendLine("        _check_pair(_world[_i], _world[_j])");
            sb.AppendLine();
            sb.AppendLine("try:");
            sb.AppendLine("    result.save('assembly_progress.step', 'STEP')");
            sb.AppendLine("except Exception as e:");
            sb.AppendLine("    print(f'STEP export failed: {e}')");
            sb.AppendLine("try:");
            sb.AppendLine("    result.save('assembly_progress.glb', 'GLTF')");
            sb.AppendLine("except Exception as e:");
            sb.AppendLine("    print(f'GLB export failed: {e}')");
            return sb.ToString();
        }

        // ───────────────────────── in-loop candidate collision check ─────────────────────────

        /// <summary>
        /// Standalone collision script for a freshly generated part: places the candidate and all
        /// approved neighbours via blueprint poses and checks every candidate↔neighbour pair plus
        /// candidate instance self-pairs. Emits STRATUM_COLLISION json lines.
        /// </summary>
        public static string BuildCollisionScript(StratumCompositionEntry candidate, List<StratumCompositionEntry> neighbors)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(PlacementPrologue);
            sb.AppendLine("_candidate_count = 0");
            EmitPlacements(sb, candidate, 0);
            sb.AppendLine("_candidate_count = len(_placed)");
            int idx = 0;
            foreach (var n in neighbors)
            {
                idx++;
                if (n.Slot != null && n.Slot.Virtual) continue;
                EmitPlacements(sb, n, idx);
            }
            sb.AppendLine(CollisionCheckSnippet);
            sb.AppendLine("for _i in range(_candidate_count):");
            sb.AppendLine("    for _j in range(len(_world)):");
            sb.AppendLine("        if _j <= _i:");
            sb.AppendLine("            continue");
            sb.AppendLine("        _check_pair(_world[_i], _world[_j])");
            sb.AppendLine("print('STRATUM_COLLISION_DONE')");
            return sb.ToString();
        }

        public static List<StratumCollisionPair> ParseCollisionReport(string? stdout)
        {
            var list = new List<StratumCollisionPair>();
            if (string.IsNullOrEmpty(stdout)) return list;
            foreach (var raw in stdout.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (!line.StartsWith("STRATUM_COLLISION:", StringComparison.Ordinal)) continue;
                try
                {
                    var rec = JsonConvert.DeserializeObject<StratumCollisionPair>(line.Substring("STRATUM_COLLISION:".Length));
                    if (rec != null) list.Add(rec);
                }
                catch { /* skip malformed line */ }
            }
            return list;
        }

        /// <summary>
        /// Applies the contract-aware collision policy: contract-joined pairs may touch
        /// (intersection ≤ 1 mm³); every other pair must neither intersect nor come closer
        /// than the registry's min_clearance. Returns failure strings for the repair prompt.
        /// </summary>
        public static List<string> EvaluateCollisions(
            List<StratumCollisionPair> pairs, MechanicalBlueprint blueprint, StratumDimensionRegistry registry)
        {
            const double interpenetrationEpsilonMm3 = 1.0;
            double minClearance = registry.Get("min_clearance", 0.5);
            var contractPairs = StratumContractEngine.ContractPairs(blueprint);
            var failures = new List<string>();

            foreach (var p in pairs)
            {
                if (p.Method == "error") { failures.Add($"Collision check between {p.A} and {p.B} errored: {p.Error}"); continue; }
                var key = PairKey(p.ATask, p.BTask);
                bool joined = contractPairs.Contains(key);
                bool samePart = string.Equals(p.ATask, p.BTask, StringComparison.OrdinalIgnoreCase);

                if (p.IntersectionMm3 > interpenetrationEpsilonMm3)
                {
                    failures.Add(joined
                        ? $"`{p.ATask}` and `{p.BTask}` share a contract (contact allowed) but INTERPENETRATE by {p.IntersectionMm3:0.#} mm³ — the parts overlap in space; shrink or reposition geometry so they only touch at the contract interface."
                        : $"`{p.A}` and `{p.B}` INTERPENETRATE by {p.IntersectionMm3:0.#} mm³ at their blueprint placements. Parts must not occupy the same space — stay inside your slot envelope.");
                }
                else if (!joined && !samePart && p.MinClearanceMm >= 0 && p.MinClearanceMm < minClearance && p.Method != "aabb")
                {
                    failures.Add($"`{p.A}` and `{p.B}` are only {p.MinClearanceMm:0.##} mm apart (minimum clearance is {minClearance:0.##} mm and they share no contract). Add clearance, or declare a contract if they are meant to mate.");
                }
            }
            return failures;
        }

        private static (string, string) PairKey(string a, string b)
        {
            var x = (a ?? "").Trim().ToLowerInvariant();
            var y = (b ?? "").Trim().ToLowerInvariant();
            return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
        }

        // ───────────────────────── rendering (matplotlib Agg, headless) ─────────────────────────

        /// <summary>
        /// Renders the staged parts (placed via blueprint poses) as a 2×2 grid — isometric +
        /// top/front/right orthographic views with mm axes — to a PNG. Matplotlib Agg only:
        /// no GPU/OpenGL, safe on a headless Windows host.
        /// </summary>
        public static string BuildRenderScript(List<StratumCompositionEntry> entries, string outPngName, string title)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("import matplotlib");
            sb.AppendLine("matplotlib.use('Agg')");
            sb.AppendLine("import matplotlib.pyplot as plt");
            sb.AppendLine("from matplotlib.patches import Patch");
            sb.AppendLine("import numpy as np");
            sb.AppendLine(PlacementPrologue);
            int idx = 0;
            foreach (var e in entries)
            {
                idx++;
                if (e.Slot != null && e.Slot.Virtual) continue;
                EmitPlacements(sb, e, idx);
            }
            sb.AppendLine();
            sb.AppendLine("_palette = [" + string.Join(", ", Palette.Select(PyStr)) + "]");
            sb.AppendLine(@"
_meshes = []  # (name, color, V, F)
_task_colors = {}
for (_name, _task, _wp, _t) in _placed:
    if _task not in _task_colors:
        _task_colors[_task] = _palette[len(_task_colors) % len(_palette)]
    try:
        _c = _to_world_compound(_wp, _t)
        _verts, _tris = _c.tessellate(0.8)
        _V = np.array([(v.x, v.y, v.z) for v in _verts])
        _F = np.array(_tris, dtype=int)
        if len(_V) and len(_F):
            _meshes.append((_name, _task_colors[_task], _V, _F))
    except Exception as _e:
        print(f'STRATUM_RENDER_MESH_FAILED:{_name}: {_e}')

if not _meshes:
    raise RuntimeError('nothing to render')

_allV = np.vstack([m[2] for m in _meshes])
_mins = _allV.min(axis=0); _maxs = _allV.max(axis=0)
_span = np.maximum(_maxs - _mins, 1.0)

_views = [('Isometric', 30, -60), ('Top (XY)', 90, -90), ('Front (XZ)', 0, -90), ('Right (YZ)', 0, 0)]
_fig = plt.figure(figsize=(10, 10), dpi=100)
for _i, (_vt, _elev, _azim) in enumerate(_views):
    _ax = _fig.add_subplot(2, 2, _i + 1, projection='3d')
    for (_name, _color, _V, _F) in _meshes:
        _ax.plot_trisurf(_V[:, 0], _V[:, 1], _V[:, 2], triangles=_F,
                         color=_color, edgecolor='none', linewidth=0, antialiased=False, shade=True, alpha=0.95)
    _ax.set_xlim(_mins[0], _maxs[0]); _ax.set_ylim(_mins[1], _maxs[1]); _ax.set_zlim(_mins[2], _maxs[2])
    _ax.set_box_aspect((_span[0], _span[1], _span[2]))
    _ax.set_xlabel('X (mm)'); _ax.set_ylabel('Y (mm)'); _ax.set_zlabel('Z (mm)')
    _ax.set_title(_vt, fontsize=10)
    _ax.view_init(elev=_elev, azim=_azim)

_handles = [Patch(facecolor=c, label=t) for t, c in _task_colors.items()]
_fig.legend(handles=_handles, loc='lower center', ncol=min(len(_handles), 4), fontsize=8)
");
            sb.AppendLine($"_fig.suptitle({PyStr(title)}, fontsize=11)");
            sb.AppendLine($"_fig.savefig(r'{outPngName}', bbox_inches='tight')");
            sb.AppendLine("plt.close(_fig)");
            sb.AppendLine($"print('STRATUM_RENDER_OK:{outPngName}')");
            return sb.ToString();
        }

        // ───────────────────────── measurement queries ─────────────────────────

        /// <summary>Bounding box of a STEP file → STRATUM_MEASURE json line.</summary>
        public static string BuildMeasureBBoxScript(string stepFileName)
        {
            return PlacementPrologue + $@"
import json as _json
_c = cq.Compound.makeCompound(cq.importers.importStep(r'{stepFileName}').vals())
_bb = _c.BoundingBox()
print('STRATUM_MEASURE:' + _json.dumps({{
    'query': 'bbox',
    'dx': round(_bb.xmax - _bb.xmin, 3), 'dy': round(_bb.ymax - _bb.ymin, 3), 'dz': round(_bb.zmax - _bb.zmin, 3),
    'xmin': round(_bb.xmin, 3), 'xmax': round(_bb.xmax, 3),
    'ymin': round(_bb.ymin, 3), 'ymax': round(_bb.ymax, 3),
    'zmin': round(_bb.zmin, 3), 'zmax': round(_bb.zmax, 3),
    'volumeMm3': round(_occ_vol(_c.wrapped), 3),
}}))
";
        }

        /// <summary>Min distance + intersection between two placed STEPs → STRATUM_MEASURE json line.</summary>
        public static string BuildMeasureDistanceScript(StratumCompositionEntry a, StratumCompositionEntry b)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(PlacementPrologue);
            EmitPlacements(sb, a, 0);
            EmitPlacements(sb, b, 1);
            sb.AppendLine(@"
import json as _json
_ca = _to_world_compound(_placed[0][2], _placed[0][3])
_cb = _to_world_compound(_placed[-1][2], _placed[-1][3])
_iv = _occ_common_vol(_ca.wrapped, _cb.wrapped)
_md = _occ_min_dist(_ca.wrapped, _cb.wrapped)
print('STRATUM_MEASURE:' + _json.dumps({
    'query': 'distance',
    'a': _placed[0][0], 'b': _placed[-1][0],
    'intersectionMm3': round(_iv, 3) if _iv is not None else None,
    'minDistanceMm': round(_md, 3) if _md is not None else None,
}))
");
            return sb.ToString();
        }

        // ───────────────────────── helpers ─────────────────────────

        private static double G(double[]? v, int i) => v != null && v.Length > i ? v[i] : 0;
        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        private static string PyStr(string s) =>
            "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ").Replace("\r", "") + "'";
    }
}
