using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace ValheimPlanner;

/// <summary>
/// Optional export for players who use the PlanBuild mod (it places the plan as ghosts in the world).
/// Format: PlanBuild's ".blueprint" text file — header lines, then "#Pieces" and one line per piece:
///   prefab;category;x;y;z;qx;qy;qz;qw;additionalInfo;sx;sy;sz
/// Positions are Unity metres relative to the blueprint centre (min corner), rotations Unity quaternions.
/// PlanBuild scans the Valheim folder recursively, so dropping the file there is enough.
/// </summary>
public static class PlanBuildExport
{
    static readonly string[] Categories = { "Misc", "Crafting", "BuildingWorkbench", "BuildingStonecutter", "Furniture", "DeepNorth", "Feasts", "Food", "Meads" };
    static string F(float v) => MathF.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    public static string Build(string name, IEnumerable<(PieceDef Def, Vector3 Pos, int Rot)> pieces, float rotStepDeg)
    {
        // Back to Unity space: mirror Z; a Godot Y rotation of a is a Unity Y rotation of -a.
        var list = pieces.Select(p => (p.Def, Pos: new Vector3(p.Pos.X, p.Pos.Y, -p.Pos.Z), Angle: -p.Rot * rotStepDeg)).ToList();
        var min = list.Count == 0 ? Vector3.Zero : new Vector3(list.Min(p => p.Pos.X), list.Min(p => p.Pos.Y), list.Min(p => p.Pos.Z));
        var sb = new StringBuilder();
        sb.Append("#Name:").Append(name.Replace("\n", " ")).Append('\n');
        sb.Append("#Creator:").Append(AppInfo.Name).Append('\n');
        sb.Append("#Description:\"").Append(Loc.F("Planned with {0} — {1} pieces", AppInfo.Name, list.Count).Replace("\"", "'")).Append("\"\n");
        sb.Append("#Category:").Append(AppInfo.Name).Append('\n');
        sb.Append("#Pieces\n");
        foreach (var p in list.OrderBy(p => p.Pos.Y).ThenBy(p => p.Pos.X).ThenBy(p => p.Pos.Z))
        {
            var rel = p.Pos - min;
            float half = Mathf.DegToRad(p.Angle) / 2;
            string cat = p.Def.Category >= 0 && p.Def.Category < Categories.Length ? Categories[p.Def.Category] : "Misc";
            sb.Append(p.Def.Prefab).Append(';').Append(cat).Append(';')
              .Append(F(rel.X)).Append(';').Append(F(rel.Y)).Append(';').Append(F(rel.Z)).Append(';')
              .Append("0;").Append(F(MathF.Sin(half))).Append(";0;").Append(F(MathF.Cos(half))).Append(';')
              .Append("\"\";1;1;1\n");
        }
        return sb.ToString();
    }

    /// <summary>Folders where PlanBuild will find the file: the Valheim folder (manual install) and mod-manager profiles that have PlanBuild.</summary>
    public static List<(string Label, string Dir, bool HasPlanBuild)> Targets(string? gameDir)
    {
        var result = new List<(string, string, bool)>();
        if (gameDir != null && Directory.Exists(gameDir))
        {
            bool has = HasPlanBuild(Path.Combine(gameDir, "BepInEx", "plugins"));
            result.Add(("Valheim", Path.Combine(gameDir, "BepInEx", "config", "PlanBuild", "blueprints"), has));
        }
        string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        foreach (var (manager, root) in new[]
        {
            ("r2modman", Path.Combine(appData, "r2modmanPlus-local", "Valheim", "profiles")),
            ("Thunderstore", Path.Combine(appData, "Thunderstore Mod Manager", "DataFolder", "Valheim", "profiles")),
        })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var profile in Directory.GetDirectories(root))
                if (HasPlanBuild(Path.Combine(profile, "BepInEx", "plugins")))
                    result.Add(($"{manager} · {Path.GetFileName(profile)}", Path.Combine(profile, "BepInEx", "config", "PlanBuild", "blueprints"), true));
        }
        return result;
    }

    static bool HasPlanBuild(string pluginsDir)
    {
        try { return Directory.Exists(pluginsDir) && Directory.EnumerateFiles(pluginsDir, "PlanBuild*.dll", SearchOption.AllDirectories).Any(); }
        catch (Exception) { return false; }
    }

    public sealed record Imported(string Name, List<(string Prefab, Vector3 Pos, int Rot)> Pieces, int Tilted, int OffGrid);

    /// <summary>
    /// Reads a PlanBuild ".blueprint" or BuildShare ".vbuild" file into planner coordinates (Godot space,
    /// lowest piece origin at the origin). Vanilla pieces only turn around the vertical axis in 22.5° steps,
    /// so other rotations are rounded and counted.
    /// </summary>
    public static Imported Parse(string text, bool vbuild, float rotStepDeg)
    {
        string name = "";
        var raw = new List<(string Prefab, Vector3 Pos, Quaternion Rot)>();
        bool inPieces = true;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#Name:")) { name = line[6..].Trim(); continue; }
            if (line.StartsWith("#Pieces")) { inPieces = true; continue; }
            if (line.StartsWith("#")) { inPieces = false; continue; }   // creator, description, snap points, terrain…
            if (!inPieces) continue;
            float N(string s) => float.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0f;
            if (vbuild)
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 8) continue;
                raw.Add((f[0], new Vector3(N(f[5]), N(f[6]), N(f[7])), new Quaternion(N(f[1]), N(f[2]), N(f[3]), N(f[4]))));
            }
            else
            {
                var f = line.Replace(',', '.').Split(';');
                if (f.Length < 9) continue;
                string prefab = f[0].Split('(')[0].Trim();
                raw.Add((prefab, new Vector3(N(f[2]), N(f[3]), N(f[4])), new Quaternion(N(f[5]), N(f[6]), N(f[7]), N(f[8]))));
            }
        }
        if (raw.Count == 0) return new Imported(name, new(), 0, 0);
        var min = new Vector3(raw.Min(p => p.Pos.X), raw.Min(p => p.Pos.Y), raw.Min(p => p.Pos.Z));
        int tilted = 0, offGrid = 0;
        var pieces = new List<(string, Vector3, int)>();
        foreach (var (prefab, pos, q0) in raw)
        {
            var q = q0.LengthSquared() > 1e-6f ? q0.Normalized() : Quaternion.Identity;
            // Unity yaw around +Y; a Unity yaw of b is a Godot yaw of -b.
            float yaw = Mathf.RadToDeg(Mathf.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.Y * q.Y + q.X * q.X)));
            float tilt = 1 - 2 * (q.X * q.X + q.Z * q.Z);          // y component of the rotated up vector
            if (tilt < 0.999f) tilted++;
            float steps = -yaw / rotStepDeg;
            if (Mathf.Abs(steps - Mathf.Round(steps)) > 0.05f) offGrid++;
            int rot = (((int)Mathf.Round(steps)) % 16 + 16) % 16;
            var rel = pos - min;
            pieces.Add((prefab, new Vector3(rel.X, rel.Y, -rel.Z), rot));
        }
        return new Imported(name, pieces, tilted, offGrid);
    }

    public static string SafeFileName(string name)
    {
        var s = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('_');
        return s.Length == 0 ? "plan" : s;
    }
}
