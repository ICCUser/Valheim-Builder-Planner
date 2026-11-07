using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ValheimPlanner;

/// <summary>Orders pieces into the sequence a player builds them: ground first, roof and furniture last.</summary>
public static class BuildSteps
{
    public static readonly string[] Names =
    {
        "Floors and foundations", "Stairs", "Poles and pillars", "Walls", "Doors and openings",
        "Beams", "Roof", "Crafting stations", "Furniture and decoration", "Miscellaneous",
    };

    public static int StepOf(PieceDef d)
    {
        string p = d.Prefab.ToLowerInvariant(), l = d.EnglishName.ToLowerInvariant();
        bool Has(params string[] k) => k.Any(x => p.Contains(x) || l.Contains(x));
        if (d.Category == 1) return 7;                                   // crafting stations
        if (d.Category == 4) return 8;                                   // furniture
        if (Has("stair", "escalier", "ladder", "échelle")) return 1;
        if (Has("door", "gate", "porte", "portail", "window", "fenêtre", "arch")) return 4;
        if (Has("roof", "toit") && !Has("wall", "mur")) return 6;
        if (Has("floor", "plancher", "sol ", "foundation", "fondation")) return 0;
        if (Has("pole", "poteau", "pillar", "pilier", "column", "colonne")) return 2;
        if (Has("beam", "poutre", "log", "bûche", "cross", "croix")) return 5;
        if (Has("wall", "mur", "palisade", "stake")) return 3;
        return d.Category == 0 ? 9 : 8;
    }

    public record Line(PieceDef Def, int Count);

    /// <summary>Steps of one floor: (step index, pieces) in build order.</summary>
    public static IEnumerable<(int Step, List<Line> Lines)> ForFloor(IEnumerable<PieceDef> pieces) =>
        pieces.GroupBy(StepOf).OrderBy(g => g.Key)
              .Select(g => (g.Key, g.GroupBy(d => d).Select(x => new Line(x.Key, x.Count())).OrderByDescending(x => x.Count).ToList()));

    static string Costs(IEnumerable<PieceDef> pieces) =>
        string.Join(", ", pieces.SelectMany(p => p.Costs).GroupBy(c => c.Label)
                               .Select(g => (g.Key, N: g.Sum(c => c.Amount))).OrderByDescending(t => t.N).Select(t => $"{t.N} {t.Key}"));

    /// <summary>Plain-text checklist to keep on a second screen or print.</summary>
    public static string Export(IEnumerable<(PieceDef Def, int Level)> placed, float levelHeight)
    {
        var all = placed.ToList();
        var sb = new StringBuilder();
        sb.AppendLine(AppInfo.Name.ToUpperInvariant() + " — " + Loc.T("BUILD LIST"));
        sb.AppendLine(Loc.F("Generated on {0}", DateTime.Now.ToString("g", Loc.Culture)));
        sb.AppendLine();
        sb.AppendLine(Loc.F("Pieces: {0}", all.Count));
        sb.AppendLine(Loc.F("Materials to gather: {0}", Costs(all.Select(a => a.Def))));
        var stations = all.Select(a => a.Def.Station).Where(s => s != null).Distinct().ToList();
        if (stations.Count > 0) sb.AppendLine(Loc.F("Stations nearby: {0}", string.Join(", ", stations)));
        int step = 1;
        foreach (var floor in all.GroupBy(a => a.Level).OrderBy(g => g.Key))
        {
            sb.AppendLine();
            sb.AppendLine("══════ " + Loc.F("FLOOR {0}  (from {1} to {2} m)", floor.Key + 1, floor.Key * levelHeight, (floor.Key + 1) * levelHeight) + " ══════");
            sb.AppendLine(Loc.F("Materials: {0}", Costs(floor.Select(a => a.Def))));
            foreach (var (s, lines) in ForFloor(floor.Select(a => a.Def)))
            {
                sb.AppendLine();
                sb.AppendLine("  [ ] " + Loc.F("Step {0} — {1}", step++, Loc.T(Names[s])));
                foreach (var ln in lines)
                    sb.AppendLine($"        {ln.Count,3} × {ln.Def.Label}   ({string.Join(", ", ln.Def.Costs.Select(c => $"{c.Amount * ln.Count} {c.Label}"))})");
            }
        }
        return sb.ToString();
    }
}
