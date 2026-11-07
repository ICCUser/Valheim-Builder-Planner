using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace ValheimPlanner;

/// <summary>Oriented box in piece space (Godot coordinates).</summary>
public readonly record struct BoxDef(Vector3 Center, Vector3 Size, Quaternion Rotation, bool IsMesh = false, bool Fake = false);

public readonly record struct Cost(string Item, Dictionary<string, string>? Names, int Amount)
{
    public string Label => Loc.Game(Names, Item);
}

public sealed record CategoryDef(int Id, Dictionary<string, string>? Names, string Raw)
{
    public string Label => Raw == "DEEPNORTH" ? Loc.T("Deep North") : Loc.Game(Names, Raw.TrimStart('$'));
    public void Deconstruct(out int id, out string label) { id = Id; label = Label; }
}

public sealed class MaterialProps
{
    public float MaxSupport, MinSupport, VerticalLoss, HorizontalLoss;
}

public sealed class PieceDef
{
    public required string Prefab { get; init; }
    public Dictionary<string, string>? Names { get; init; }
    public string Label => Loc.Game(Names, Prefab);
    /// <summary>Stable English name, used for rules that look at the name (thatch, stairs…).</summary>
    public string EnglishName => Names != null && Names.TryGetValue("English", out var e) ? e : Prefab;
    public required int Category { get; init; }
    public Dictionary<string, string>? StationNames { get; init; }
    public string? Station => StationNames == null ? null : Loc.Game(StationNames, "?");
    public string? Material { get; init; }
    public bool Supports { get; init; } = true;
    public Vector3 ComOffset { get; init; }
    public bool ForceCorrectCom { get; set; }
    public List<Cost> Costs { get; } = new();
    public List<Vector3> Snaps { get; } = new();
    public List<BoxDef> Boxes { get; } = new();
    /// <summary>Radius of a sphere around the origin that contains every box.</summary>
    public float Radius { get; set; }
    /// <summary>Gable walls: the triangle (from the 3 snap points) that the game fills with planks.</summary>
    public Vector3[]? Triangle { get; set; }
    /// <summary>Grid size in metres, e.g. [2, 2] for a wall.</summary>
    public float[] Dims { get; set; } = System.Array.Empty<float>();
    /// <summary>Human readable size in the current language, e.g. "2×2 m" or "1,5×1 m".</summary>
    public string SizeText => Dims.Length == 0 ? "" : string.Join("×", Dims.Select(d => d.ToString("0.#", Loc.Culture))) + " m";
}

public sealed class PieceCatalog
{
    public List<PieceDef> Pieces { get; } = new();
    public Dictionary<string, PieceDef> ByPrefab { get; } = new();
    public List<CategoryDef> Categories { get; } = new();
    public Dictionary<string, MaterialProps> Materials { get; } = new();

    // Unity is left-handed (+Z forward); Godot is right-handed (-Z forward). Mirror Z.
    static Vector3 Pos(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), -a[2].GetSingle());
    static Vector3 Size(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    static Quaternion Rot(JsonElement a) => new Quaternion(-a[0].GetSingle(), -a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle()).Normalized();

    public static PieceCatalog Load(string json)
    {
        var cat = new PieceCatalog();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Structural rules of each material (support values and losses per metre), as the game applies them.
        foreach (var (name, max, min, vert, horiz) in new[]
        {
            ("Wood", 100f, 10f, 0.125f, 0.2f), ("HardWood", 140f, 10f, 0.1f, 1f / 6f), ("Stone", 1000f, 100f, 0.125f, 1f),
            ("Iron", 1500f, 20f, 1f / 13f, 1f / 13f), ("Marble", 1500f, 100f, 0.125f, 0.5f), ("Ashstone", 2000f, 100f, 0.1f, 1f / 3f),
            ("Ancient", 5000f, 100f, 1f / 15f, 0.25f), ("Ice", 1000f, 100f, 0.125f, 1f / 3f), ("Timberwood", 200f, 10f, 1f / 13f, 0.2f),
        })
            cat.Materials[name] = new MaterialProps { MaxSupport = max, MinSupport = min, VerticalLoss = vert, HorizontalLoss = horiz };

        foreach (var c in root.GetProperty("categories").EnumerateArray())
        {
            cat.Categories.Add(new CategoryDef(c.GetProperty("id").GetInt32(), Names(c), c.GetProperty("label").GetString()!));
        }

        foreach (var p in root.GetProperty("pieces").EnumerateArray())
        {
            if (!p.GetProperty("enabled").GetBoolean()) continue;
            string prefab = p.GetProperty("prefab").GetString()!;
            if (prefab == "piece_repair") continue;

            p.TryGetProperty("stability", out var st);
            var def = new PieceDef
            {
                Prefab = prefab,
                Names = Names(p),
                Category = p.GetProperty("category").GetInt32(),
                StationNames = Names(p, "stationNames"),
                Material = st.ValueKind == JsonValueKind.Object ? st.GetProperty("material").GetString() : null,
                Supports = st.ValueKind != JsonValueKind.Object || st.GetProperty("supports").GetBoolean(),
                ComOffset = st.ValueKind == JsonValueKind.Object ? Pos(st.GetProperty("comOffset")) : Vector3.Zero,
            };
            foreach (var r in p.GetProperty("resources").EnumerateArray())
            {
                string item = r.GetProperty("item").GetString()!;
                def.Costs.Add(new Cost(item, Names(r), r.GetProperty("amount").GetInt32()));
            }
            foreach (var s in p.GetProperty("snapPoints").EnumerateArray())
                def.Snaps.Add(Pos(s.GetProperty("pos")));

            var cols = p.GetProperty("colliders").EnumerateArray().ToList();
            var active = cols.Where(c => c.GetProperty("active").GetBoolean()).ToList();
            foreach (var c in active.Count > 0 ? active : cols)
                def.Boxes.Add(new BoxDef(Pos(c.GetProperty("center")), Size(c.GetProperty("size")), Rot(c.GetProperty("rotation")),
                    IsMesh: c.GetProperty("shape").GetString() == "mesh"));
            if (def.Boxes.Count == 0)
                def.Boxes.Add(new BoxDef(new Vector3(0, 0.25f, 0), new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, Fake: true));
            def.ForceCorrectCom = st.ValueKind == JsonValueKind.Object && st.GetProperty("forceCorrectCom").GetBoolean();

            def.Radius = def.Boxes.Max(b => b.Center.Length() + b.Size.Length() * 0.5f);
            foreach (var s in def.Snaps) def.Radius = Mathf.Max(def.Radius, s.Length());

            // Gable walls only have colliders on their edges; the plank fill is a render mesh.
            // Their 3 snap points are exactly the triangle corners, so rebuild the fill from them.
            string pl = prefab.ToLowerInvariant();
            if (def.Snaps.Count == 3 && pl.Contains("wall") && (pl.Contains("roof") || pl.Contains("_26") || pl.Contains("_45") || pl.Contains("_67"))
                && !pl.Contains("top") && !pl.Contains("cross") && !pl.Contains("beam"))
                def.Triangle = def.Snaps.ToArray();

            def.Dims = ComputeSize(def);

            cat.Pieces.Add(def);
            cat.ByPrefab[prefab] = def;
        }
        return cat;
    }

    static Dictionary<string, string>? Names(JsonElement e, string prop = "names")
    {
        if (!e.TryGetProperty(prop, out var n) || n.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, string>();
        foreach (var kv in n.EnumerateObject()) d[kv.Name] = kv.Value.GetString() ?? "";
        return d;
    }

    /// <summary>Axis-aligned extent of the piece, ignoring thin dimensions (planks, wall thickness).</summary>
    static float[] ComputeSize(PieceDef def)
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = -min;
        void Add(Vector3 p) { min = min.Min(p); max = max.Max(p); }
        // Snap points sit on the building grid, so they give the "game" size (2×2, 2×1…); fall back to geometry.
        if (def.Snaps.Count >= 2)
        {
            foreach (var s in def.Snaps) Add(s);
            var se = max - min;
            if (Mathf.Max(se.X, Mathf.Max(se.Y, se.Z)) >= 0.45f) return Format(se);
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); max = -min;
        }
        foreach (var b in def.Boxes)
        {
            var basis = new Basis(b.Rotation);
            var h = b.Size / 2;
            for (int i = 0; i < 8; i++)
                Add(b.Center + basis * new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z));
        }
        return Format(max - min);
    }

    static float[] Format(Vector3 e) =>
        new[] { e.X, e.Y, e.Z }.Where(d => d >= 0.45f).Select(d => Mathf.Snapped(d, 0.5f)).ToArray();
}
