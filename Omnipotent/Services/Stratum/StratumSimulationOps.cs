using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// FEA plumbing (gmsh meshing + CalculiX solving + result summarising) — extracted from
    /// the legacy StratumSimulationAgent so the Stratum Engineer's run_fea tool owns one copy.
    /// </summary>
    public static class StratumSimulationOps
    {
        /// <summary>Meshes a STEP file into a CalculiX-format mesh.inp via gmsh.</summary>
        public static async Task<(bool ok, string meshInpPath, string error)> MeshStepAsync(
            StratumToolManager tools, byte[] stepBytes, string runDir, CancellationToken ct, Action<string>? progress = null)
        {
            if (tools.Status().GmshPath == null)
            {
                progress?.Invoke("Installing gmsh (one-time)…");
                await tools.EnsureGmshAsync(msg => progress?.Invoke(msg), ct);
            }
            string gmshExe = tools.ResolveGmshOrThrow();

            Directory.CreateDirectory(runDir);
            string stepLocal = Path.Combine(runDir, "geom.step");
            File.WriteAllBytes(stepLocal, stepBytes);
            string meshInp = Path.Combine(runDir, "mesh.inp");

            var meshResult = await ProcessRunner.RunAsync(gmshExe, new[]
            {
                stepLocal, "-3", "-format", "inp", "-o", meshInp, "-clmax", "5"
            }, runDir, TimeSpan.FromMinutes(5), null, ct);
            if (meshResult.exit != 0 || !File.Exists(meshInp))
                return (false, meshInp, $"gmsh meshing failed (exit {meshResult.exit}): {Tail(meshResult.stderr, 800)}");
            return (true, meshInp, "");
        }

        /// <summary>Combines the mesh + deck into a job file and runs ccx once.</summary>
        public static async Task<(bool solved, List<FileInfo> outputs, string summary, string combinedLog)> SolveAsync(
            StratumToolManager tools, string runDir, string meshInpPath, string deck, int version,
            TimeSpan timeout, CancellationToken ct, Action<string>? progress = null)
        {
            if (tools.Status().CalculixPath == null)
            {
                progress?.Invoke("Installing CalculiX (one-time)…");
                await tools.EnsureCalculixAsync(msg => progress?.Invoke(msg), ct);
            }
            string ccxExe = tools.ResolveCalculixOrThrow();

            string jobInp = Path.Combine(runDir, $"job_v{version}.inp");
            string includeLine = $"*INCLUDE, INPUT={Path.GetFileName(meshInpPath)}\n";
            File.WriteAllText(jobInp, includeLine + deck);

            string jobName = Path.GetFileNameWithoutExtension(jobInp);
            var r = await ProcessRunner.RunAsync(ccxExe, new[] { "-i", jobName }, runDir, timeout, null, ct);
            string combinedLog = $"--- stdout ---\n{r.stdout}\n--- stderr ---\n{r.stderr}";

            var outputs = Directory.GetFiles(runDir, jobName + ".*")
                .Where(p => Path.GetFileName(p) != Path.GetFileName(jobInp))
                .Select(p => new FileInfo(p))
                .ToList();

            bool solved = r.exit == 0 && outputs.Any(f => f.Extension.Equals(".frd", StringComparison.OrdinalIgnoreCase));
            string summary = solved
                ? SummariseDat(outputs.FirstOrDefault(f => f.Extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)))
                : "";
            return (solved, outputs, summary, combinedLog);
        }

        /// <summary>First ~80 lines of the mesh (reveals the *NSET/*ELSET names the deck can target).</summary>
        public static string ReadMeshIntro(string meshInp)
        {
            try
            {
                using var sr = new StreamReader(meshInp);
                var sb = new StringBuilder();
                int lines = 0;
                string? l;
                while ((l = sr.ReadLine()) != null && lines < 80)
                {
                    sb.AppendLine(l);
                    lines++;
                }
                sb.AppendLine("# … (mesh continues; full file lives at mesh.inp)");
                return sb.ToString();
            }
            catch { return ""; }
        }

        public static string SummariseDat(FileInfo? dat)
        {
            if (dat == null || !dat.Exists) return "Solve completed; no .dat summary file produced.";
            try
            {
                var lines = File.ReadAllLines(dat.FullName);
                var sb = new StringBuilder();
                sb.AppendLine($"CalculiX solve OK. Result file: {dat.Name} ({dat.Length / 1024} KB).");
                int kept = 0;
                foreach (var ln in lines)
                {
                    if (kept >= 12) break;
                    if (ln.Contains("stress", StringComparison.OrdinalIgnoreCase)
                        || ln.Contains("displacement", StringComparison.OrdinalIgnoreCase)
                        || ln.Contains("maximum", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine(ln.TrimEnd());
                        kept++;
                    }
                }
                if (kept == 0) sb.AppendLine("(.frd written for full field; .dat had no max-stress / max-displacement lines — add *NODE PRINT in the deck for summary metrics.)");
                return sb.ToString();
            }
            catch (Exception ex) { return $"Solve completed; failed to read .dat ({ex.Message})."; }
        }

        private static string Tail(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : "…" + s[^max..]);
    }
}
