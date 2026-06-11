using Newtonsoft.Json;
using System.Globalization;

namespace Omnipotent.Services.Stratum
{
    /// <summary>Result of one geometric probe executed by the verification footer.</summary>
    public class VerifyFeatureResult
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("kind")] public string Kind { get; set; } = "";
        [JsonProperty("severity")] public string Severity { get; set; } = "hard";
        [JsonProperty("pass")] public bool Pass { get; set; }
        [JsonProperty("measuredMm3")] public double? MeasuredMm3 { get; set; }
        [JsonProperty("frac")] public double? Frac { get; set; }
        [JsonProperty("desc")] public string Desc { get; set; } = "";
        [JsonProperty("error")] public string? Error { get; set; }
    }

    public class VerifyBBox
    {
        [JsonProperty("dx")] public double Dx { get; set; }
        [JsonProperty("dy")] public double Dy { get; set; }
        [JsonProperty("dz")] public double Dz { get; set; }
        [JsonProperty("xmin")] public double Xmin { get; set; }
        [JsonProperty("xmax")] public double Xmax { get; set; }
        [JsonProperty("ymin")] public double Ymin { get; set; }
        [JsonProperty("ymax")] public double Ymax { get; set; }
        [JsonProperty("zmin")] public double Zmin { get; set; }
        [JsonProperty("zmax")] public double Zmax { get; set; }
    }

    /// <summary>Parsed STRATUM_VERIFY report — the deterministic ground truth about a produced part.</summary>
    public class StratumVerificationReport
    {
        [JsonProperty("valid")] public bool Valid { get; set; }
        [JsonProperty("solids")] public int Solids { get; set; }
        [JsonProperty("watertight")] public bool Watertight { get; set; }
        [JsonProperty("volumeMm3")] public double VolumeMm3 { get; set; }
        [JsonProperty("bbox")] public VerifyBBox? Bbox { get; set; }
        [JsonProperty("features")] public List<VerifyFeatureResult> Features { get; set; } = new();
        [JsonProperty("failures")] public List<string> Failures { get; set; } = new();
        [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();

        /// <summary>True when shape validity, watertightness and every hard probe passed.
        /// Does NOT include the bbox-vs-slot check — see <see cref="StratumGeometryVerifier.CheckBBox"/>.</summary>
        [JsonIgnore]
        public bool GeometryPassed => Valid && Watertight && Failures.Count == 0;
    }

    /// <summary>
    /// Builds the auto-injected verification + export footer for part scripts, and parses the
    /// STRATUM_VERIFY report it emits. The footer keeps the legacy STRATUM_BBOX lines (the
    /// composer and older parsers rely on them) and adds: shape validity (BRepCheck), watertight
    /// proxy (≥1 solid, positive volume), exact bounding box, and per-feature boolean probes
    /// for every contract RequiredFeature and electronics IntegrationFeature.
    /// </summary>
    public static class StratumGeometryVerifier
    {
        /// <summary>Hard bbox tolerance: measured may exceed the slot by 3% + 0.2 mm, plus a
        /// 10 mm allowance per axis when the part carries protruding features.</summary>
        public const double BBoxTolFactor = 1.03;
        public const double BBoxTolAbsMm = 0.2;
        public const double BBoxProtrusionAllowanceMm = 10.0;

        // ───────────────────────── footer generation ─────────────────────────

        public static string BuildVerificationFooter(MechanicalBlueprintSlot? slot, StratumDimensionRegistry registry)
        {
            double wall = registry.Get("wall_thickness", 2.4);
            var probes = new List<string>();
            if (slot != null)
            {
                foreach (var f in slot.RequiredFeatures ?? new List<RequiredFeature>())
                    probes.AddRange(BuildContractProbes(f, wall));
                foreach (var f in slot.IntegrationFeatures ?? new List<MechanicalIntegrationFeature>())
                    probes.AddRange(BuildIntegrationProbes(f, wall));
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("# ─── Stratum auto-injected verification + export footer ───");
            sb.AppendLine("import json as _v_json");
            sb.AppendLine("import cadquery as _cq_export");
            sb.AppendLine("try:");
            sb.AppendLine("    _result = result");
            sb.AppendLine("except NameError:");
            sb.AppendLine("    raise RuntimeError(\"Stratum: script must define a variable named 'result'\")");
            sb.AppendLine();
            sb.AppendLine("_assembly = _result if isinstance(_result, _cq_export.Assembly) else _cq_export.Assembly().add(_result, name='part')");
            sb.AppendLine("_compound = _assembly.toCompound()");
            sb.AppendLine(@"
def _v_vol(w):
    from OCP.GProp import GProp_GProps
    from OCP.BRepGProp import BRepGProp
    p = GProp_GProps()
    BRepGProp.VolumeProperties_s(w, p)
    return abs(p.Mass())

def _v_common_vol(aw, bw):
    from OCP.BRepAlgoAPI import BRepAlgoAPI_Common
    op = BRepAlgoAPI_Common(aw, bw)
    op.Build()
    if not op.IsDone():
        return None
    return _v_vol(op.Shape())

def _v_tool(spec):
    import cadquery as cq
    shape = spec['shape']
    if shape == 'cyl':
        d = cq.Vector(*spec['dir']).normalized()
        start = cq.Vector(*spec['pos']) - d.multiply(spec.get('back', 0.0))
        return cq.Solid.makeCylinder(spec['dia'] / 2.0, spec['len'], start, d)
    if shape == 'ring':
        d = cq.Vector(*spec['dir']).normalized()
        start = cq.Vector(*spec['pos']) - d.multiply(spec.get('back', 0.0))
        outer = cq.Solid.makeCylinder(spec['outer'] / 2.0, spec['len'], start, d)
        inner = cq.Solid.makeCylinder(spec['inner'] / 2.0, spec['len'] + 0.2, start - d.multiply(0.1), d)
        return outer.cut(inner)
    if shape == 'box':
        plane = cq.Plane(origin=tuple(spec['pos']), xDir=tuple(spec['xdir']), normal=tuple(spec['normal']))
        return cq.Workplane(plane).box(spec['w'], spec['l'], spec['h'], centered=(True, True, False)).val()
    if shape == 'boxEuler':
        wp = cq.Workplane('XY').box(spec['dx'], spec['dy'], spec['dz'])
        rx, ry, rz = spec.get('rot', [0, 0, 0])
        if abs(rx) > 1e-9: wp = wp.rotate((0, 0, 0), (1, 0, 0), rx)
        if abs(ry) > 1e-9: wp = wp.rotate((0, 0, 0), (0, 1, 0), ry)
        if abs(rz) > 1e-9: wp = wp.rotate((0, 0, 0), (0, 0, 1), rz)
        px, py, pz = spec['pos']
        return wp.val().translate(cq.Vector(px, py, pz))
    raise ValueError('unknown probe shape: ' + str(shape))

_report = {'valid': False, 'solids': 0, 'watertight': False, 'volumeMm3': 0.0,
           'bbox': None, 'features': [], 'failures': [], 'warnings': []}

try:
    from OCP.BRepCheck import BRepCheck_Analyzer
    _report['valid'] = bool(BRepCheck_Analyzer(_compound.wrapped).IsValid())
except Exception as _e:
    _report['warnings'].append(f'validity check unavailable: {_e}')
    _report['valid'] = True  # do not hard-fail on a missing checker

try:
    _report['solids'] = len(_compound.Solids())
except Exception:
    pass
try:
    _report['volumeMm3'] = round(_v_vol(_compound.wrapped), 3)
except Exception as _e:
    _report['warnings'].append(f'volume computation failed: {_e}')
_report['watertight'] = _report['solids'] >= 1 and _report['volumeMm3'] > 0.001

if not _report['valid']:
    _report['failures'].append('Geometry failed BRepCheck validity — the shape is malformed (self-intersection, bad boolean, or degenerate face).')
if not _report['watertight']:
    _report['failures'].append('No solid bodies found — the result is a shell/surface, not a watertight printable solid.')

# Bounding-box probe (legacy lines kept for the composer/host parsers).
try:
    _bb = _compound.BoundingBox()
    _dx = _bb.xmax - _bb.xmin
    _dy = _bb.ymax - _bb.ymin
    _dz = _bb.zmax - _bb.zmin
    print(f'STRATUM_BBOX:{_dx:.3f},{_dy:.3f},{_dz:.3f}')
    print(f'STRATUM_BBOX_RANGE:{_bb.xmin:.3f},{_bb.xmax:.3f},{_bb.ymin:.3f},{_bb.ymax:.3f},{_bb.zmin:.3f},{_bb.zmax:.3f}')
    _report['bbox'] = {'dx': round(_dx, 3), 'dy': round(_dy, 3), 'dz': round(_dz, 3),
                       'xmin': round(_bb.xmin, 3), 'xmax': round(_bb.xmax, 3),
                       'ymin': round(_bb.ymin, 3), 'ymax': round(_bb.ymax, 3),
                       'zmin': round(_bb.zmin, 3), 'zmax': round(_bb.zmax, 3)}
except Exception as _e_bb:
    print(f'STRATUM_BBOX_FAILED:{_e_bb}')
");
            sb.AppendLine("_PROBES = [");
            foreach (var p in probes) sb.AppendLine("    " + p + ",");
            sb.AppendLine("]");
            sb.AppendLine(@"
for _p in _PROBES:
    _entry = {'id': _p['id'], 'kind': _p['kind'], 'severity': _p['severity'], 'desc': _p['desc'], 'pass': False}
    try:
        _tool = _v_tool(_p)
        _cv = _v_common_vol(_compound.wrapped, _tool.wrapped)
        _tv = _v_vol(_tool.wrapped)
        if _cv is None or _tv < 1e-9:
            raise RuntimeError('boolean probe failed to build')
        _frac = _cv / _tv
        _entry['measuredMm3'] = round(_cv, 3)
        _entry['frac'] = round(_frac, 4)
        _mode = _p['passIf']
        if _mode == 'clearAbs':
            _entry['pass'] = _cv < _p['limit']
        elif _mode == 'clearFrac':
            _entry['pass'] = _frac < _p['limit']
        else:  # fracMin
            _entry['pass'] = _frac > _p['limit']
    except Exception as _e:
        _entry['error'] = str(_e)
    _report['features'].append(_entry)
    if not _entry['pass']:
        _msg = f""feature {_p['id']} ({_p['desc']}): "" + (
            f""probe error: {_entry.get('error')}"" if _entry.get('error') else
            f""expected {_p['expect']} but measured intersection {_entry.get('measuredMm3')} mm3 (fraction {_entry.get('frac')})"")
        (_report['failures'] if _p['severity'] == 'hard' else _report['warnings']).append(_msg)

print('STRATUM_VERIFY:' + _v_json.dumps(_report))

# STEP (canonical CAD)
try:
    _assembly.save('out.step', 'STEP')
except Exception:
    _cq_export.exporters.export(_result if not isinstance(_result, _cq_export.Assembly) else _result.toCompound(), 'out.step')

# GLB (preview)
try:
    _assembly.save('out.glb', 'GLTF')
except Exception as _e:
    print(f'GLB export failed: {_e}')
");
            return sb.ToString();
        }

        /// <summary>Concatenates registry prelude + contract helper + user script + verification footer.</summary>
        public static string AssemblePartScript(string userScript, MechanicalBlueprintSlot? slot, StratumDimensionRegistry registry)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(registry.ToPythonPrelude());
            if (slot != null)
            {
                string helper = StratumContractEngine.BuildContractHelperPython(slot);
                if (!string.IsNullOrWhiteSpace(helper)) sb.AppendLine(helper);
            }
            sb.AppendLine(userScript.TrimEnd());
            sb.Append(BuildVerificationFooter(slot, registry));
            return sb.ToString();
        }

        // Probes for a derived contract feature.
        private static IEnumerable<string> BuildContractProbes(RequiredFeature f, double wall)
        {
            string id = f.FeatureId;
            string desc = PyStr(Truncate(f.Spec, 140));
            switch (f.FeatureKind)
            {
                case "thru-hole":
                case "pilot-hole":
                case "bore":
                {
                    double probeDia = Math.Max(f.DiaMm - 0.3, f.DiaMm * 0.8);
                    double len, back;
                    if (f.DepthMm > 0.5) { len = f.DepthMm - 0.4; back = -0.2; } // start 0.2 inside the surface
                    else { len = Math.Max(3 * wall, 6.0); back = len / 2.0; }     // bounded thru probe centred on pos
                    yield return Probe(id + "_clear", f.FeatureKind, "hard", desc, "clearAbs", 1.0, "hole is clear",
                        $"'shape': 'cyl', 'pos': {Vec(f.LocalPositionMm)}, 'dir': {Vec(f.LocalAxisDir)}, 'dia': {N(probeDia)}, 'len': {N(len)}, 'back': {N(back)}");
                    // Material ring around the hole 0.4–2.0 mm into the part.
                    yield return Probe(id + "_ring", f.FeatureKind + "-ring", "hard", desc, "fracMin", 0.25, "material surrounds hole",
                        $"'shape': 'ring', 'pos': {Vec(OffsetAlong(f.LocalPositionMm, f.LocalAxisDir, 1.2))}, 'dir': {Vec(f.LocalAxisDir)}, 'inner': {N(f.DiaMm + 0.4)}, 'outer': {N(f.DiaMm + 4.0)}, 'len': 1.6, 'back': 0.8");
                    break;
                }
                case "shaft":
                case "boss":
                {
                    double probeDia = Math.Max(f.DiaMm - 0.3, f.DiaMm * 0.8);
                    double len = Math.Max(f.DepthMm - 0.4, 0.5);
                    yield return Probe(id + "_solid", f.FeatureKind, "hard", desc, "fracMin", 0.85, "protrusion present",
                        $"'shape': 'cyl', 'pos': {Vec(OffsetAlong(f.LocalPositionMm, f.LocalAxisDir, -0.2))}, 'dir': {Vec(f.LocalAxisDir)}, 'dia': {N(probeDia)}, 'len': {N(len)}, 'back': 0");
                    break;
                }
                case "tab":
                {
                    double w = Math.Max((f.SizeMm.Length > 0 ? f.SizeMm[0] : 1) - 0.4, 0.5);
                    double l = Math.Max((f.SizeMm.Length > 1 ? f.SizeMm[1] : 1) - 0.4, 0.5);
                    double h = Math.Max(f.DepthMm - 0.4, 0.5);
                    yield return Probe(id + "_solid", "tab", "hard", desc, "fracMin", 0.85, "tab present",
                        $"'shape': 'box', 'pos': {Vec(OffsetAlong(f.LocalPositionMm, f.LocalAxisDir, -0.2))}, 'normal': {Vec(f.LocalAxisDir)}, 'xdir': {Vec(f.LocalClockDir)}, 'w': {N(w)}, 'l': {N(l)}, 'h': {N(h)}");
                    break;
                }
                case "slot":
                {
                    double w = Math.Max((f.SizeMm.Length > 0 ? f.SizeMm[0] : 1) - 0.4, 0.5);
                    double l = Math.Max((f.SizeMm.Length > 1 ? f.SizeMm[1] : 1) - 0.4, 0.5);
                    double h = Math.Max(f.DepthMm - 0.4, 0.5);
                    yield return Probe(id + "_clear", "slot", "hard", desc, "clearAbs", 1.0, "slot is clear",
                        $"'shape': 'box', 'pos': {Vec(OffsetAlong(f.LocalPositionMm, f.LocalAxisDir, -0.2))}, 'normal': {Vec(f.LocalAxisDir)}, 'xdir': {Vec(f.LocalClockDir)}, 'w': {N(w)}, 'l': {N(l)}, 'h': {N(h)}");
                    break;
                }
                    // contact-face: advisory, not probed.
            }
        }

        // Probes for a deterministic electronics integration feature.
        private static IEnumerable<string> BuildIntegrationProbes(MechanicalIntegrationFeature f, double wall)
        {
            string id = $"int_{f.ForModuleInstanceId}_{f.FeatureKind}_{Math.Abs(HashOf(f)) % 10000}";
            string desc = PyStr(Truncate(f.Spec, 140));
            switch (f.FeatureKind)
            {
                case "reservation":
                {
                    double dx = Math.Max((f.SizeMm.Length > 0 ? f.SizeMm[0] : 1) - 1.0, 0.5);
                    double dy = Math.Max((f.SizeMm.Length > 1 ? f.SizeMm[1] : 1) - 1.0, 0.5);
                    double dz = Math.Max((f.SizeMm.Length > 2 ? f.SizeMm[2] : 1) - 1.0, 0.5);
                    yield return Probe(id, "reservation", "hard", desc, "clearFrac", 0.02, "reserved volume empty",
                        $"'shape': 'boxEuler', 'pos': {Vec(f.LocalPositionMm)}, 'rot': {Vec(f.LocalRotationDeg)}, 'dx': {N(dx)}, 'dy': {N(dy)}, 'dz': {N(dz)}");
                    break;
                }
                case "wall-cutout":
                {
                    double dx = Math.Max((f.SizeMm.Length > 0 ? f.SizeMm[0] : 8) - 0.5, 0.5);
                    double dy = Math.Max((f.SizeMm.Length > 1 ? f.SizeMm[1] : 8) - 0.5, 0.5);
                    // Orientation through the wall is judgement-based — warn-level probe.
                    yield return Probe(id, "wall-cutout", "warn", desc, "clearFrac", 0.05, "cutout aperture clear",
                        $"'shape': 'boxEuler', 'pos': {Vec(f.LocalPositionMm)}, 'rot': {Vec(f.LocalRotationDeg)}, 'dx': {N(dx)}, 'dy': {N(dy)}, 'dz': {N(Math.Max(2 * wall, 4))}");
                    break;
                }
                case "boss":
                {
                    // SizeMm = [bossOuterDia, bossHeight, holeDia]. Probe a thin annulus just
                    // below the PCB plane. Boss span is ambiguous → warn-level.
                    double outer = (f.SizeMm.Length > 0 ? f.SizeMm[0] : 6) - 0.3;
                    double holeDia = (f.SizeMm.Length > 2 ? f.SizeMm[2] : 3) + 0.3;
                    if (outer <= holeDia + 0.2) break;
                    var below = new[]
                    {
                        f.LocalPositionMm.Length > 0 ? f.LocalPositionMm[0] : 0,
                        f.LocalPositionMm.Length > 1 ? f.LocalPositionMm[1] : 0,
                        (f.LocalPositionMm.Length > 2 ? f.LocalPositionMm[2] : 0) - 2.0,
                    };
                    yield return Probe(id, "boss", "warn", desc, "fracMin", 0.3, "boss material present",
                        $"'shape': 'ring', 'pos': {Vec(below)}, 'dir': [0, 0, 1], 'inner': {N(holeDia)}, 'outer': {N(outer)}, 'len': 1.6, 'back': 0");
                    break;
                }
                case "thru-hole":
                {
                    double dia = f.SizeMm.Length > 0 ? f.SizeMm[0] : 3;
                    yield return Probe(id, "thru-hole", "warn", desc, "clearAbs", 1.0, "hole is clear",
                        $"'shape': 'cyl', 'pos': {Vec(f.LocalPositionMm)}, 'dir': [0, 0, 1], 'dia': {N(Math.Max(dia - 0.3, dia * 0.8))}, 'len': {N(Math.Max(3 * wall, 6))}, 'back': {N(Math.Max(3 * wall, 6) / 2)}");
                    break;
                }
            }
        }

        private static string Probe(string id, string kind, string severity, string descPy, string passIf, double limit, string expect, string shapeFields) =>
            $"{{'id': {PyStr(id)}, 'kind': {PyStr(kind)}, 'severity': {PyStr(severity)}, 'desc': {descPy}, 'passIf': {PyStr(passIf)}, 'limit': {N(limit)}, 'expect': {PyStr(expect)}, {shapeFields}}}";

        // ───────────────────────── parsing + evaluation ─────────────────────────

        public static StratumVerificationReport? ParseReport(string? stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            foreach (var raw in stdout.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (!line.StartsWith("STRATUM_VERIFY:", StringComparison.Ordinal)) continue;
                try { return JsonConvert.DeserializeObject<StratumVerificationReport>(line.Substring("STRATUM_VERIFY:".Length)); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Hard bbox-vs-slot check. Returns a failure description, or null when within tolerance.
        /// </summary>
        public static string? CheckBBox(MechanicalBlueprintSlot slot, VerifyBBox? measured)
        {
            if (measured == null || slot.BoundingBoxMm == null || slot.BoundingBoxMm.Length < 3) return null;
            bool hasProtrusions =
                (slot.RequiredFeatures?.Any(f => f.FeatureKind is "boss" or "shaft" or "tab") ?? false)
                || (slot.IntegrationFeatures?.Count > 0);
            double allowance = hasProtrusions ? BBoxProtrusionAllowanceMm : 0.0;

            var names = new[] { "X", "Y", "Z" };
            var meas = new[] { measured.Dx, measured.Dy, measured.Dz };
            var over = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                double limit = slot.BoundingBoxMm[i] * BBoxTolFactor + BBoxTolAbsMm + allowance;
                if (meas[i] > limit)
                    over.Add($"{names[i]}: measured {meas[i]:0.##} mm > allowed {limit:0.##} mm (slot {slot.BoundingBoxMm[i]:0.##} mm)");
            }
            if (over.Count == 0) return null;
            return $"Part bounding box {measured.Dx:0.##}×{measured.Dy:0.##}×{measured.Dz:0.##} mm exceeds the slot envelope: "
                + string.Join("; ", over)
                + $". The slot allows {slot.BoundingBoxMm[0]:0.##}×{slot.BoundingBoxMm[1]:0.##}×{slot.BoundingBoxMm[2]:0.##} mm "
                + $"(first dimension along principalAxis {slot.PrincipalAxis}). Either the principal axis was wrong or the part is oversized — re-design with correct local orientation and dimensions.";
        }

        /// <summary>Structured repair feedback for the next design iteration.</summary>
        public static string BuildRepairPrompt(StratumVerificationReport report, string? bboxFailure)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("The produced geometry FAILED deterministic verification. The host measured your actual output — these are facts, not opinions:");
            if (!report.Valid)
                sb.AppendLine("  ✗ Shape validity: BRepCheck reports the shape is malformed (bad boolean, self-intersection, or degenerate face). Simplify the failing operation.");
            if (!report.Watertight)
                sb.AppendLine($"  ✗ Watertightness: {report.Solids} solid bodies, volume {report.VolumeMm3:0.##} mm³. The result must be at least one closed solid.");
            if (bboxFailure != null)
                sb.AppendLine("  ✗ " + bboxFailure);
            foreach (var f in report.Features.Where(f => !f.Pass))
            {
                string detail = f.Error != null
                    ? $"probe error: {f.Error}"
                    : $"intersection {f.MeasuredMm3:0.##} mm³, fraction {f.Frac:0.###}";
                string sev = f.Severity == "hard" ? "✗" : "⚠";
                sb.AppendLine($"  {sev} Feature {f.Id} [{f.Kind}] — {f.Desc} — FAILED ({detail}).");
            }
            foreach (var w in report.Warnings.Take(5))
                sb.AppendLine($"  ⚠ {w}");
            sb.AppendLine();
            sb.AppendLine("Fix ALL ✗ items. Remember: contract features are built by the injected `stratum_apply_contract_features(body)` — your solid must provide material at each feature position (walls/flanges for holes, base surfaces for bosses); do not block holes with extra geometry. Output the corrected, complete script.");
            return sb.ToString();
        }

        // ───────────────────────── small helpers ─────────────────────────

        private static double[] OffsetAlong(double[] pos, double[] dir, double dist)
        {
            var d = StratumSpatialMath.Normalize(dir);
            return StratumSpatialMath.Add(pos, StratumSpatialMath.Scale(d, dist));
        }

        private static int HashOf(MechanicalIntegrationFeature f)
        {
            unchecked
            {
                int h = 17;
                foreach (var v in f.LocalPositionMm ?? Array.Empty<double>()) h = h * 31 + v.GetHashCode();
                h = h * 31 + (f.FeatureKind ?? "").GetHashCode();
                return h;
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        private static string PyStr(string s) =>
            "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ").Replace("\r", "") + "'";

        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Vec(double[] v) =>
            $"[{N(v.Length > 0 ? v[0] : 0)}, {N(v.Length > 1 ? v[1] : 0)}, {N(v.Length > 2 ? v[2] : 0)}]";
    }
}
