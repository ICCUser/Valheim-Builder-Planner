using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ValheimPlanner.GameData;

/// <summary>
/// Reads the player's own Valheim install (read-only) and writes, into a local folder:
///   pieces.json  — every Hammer piece: names in all game languages, cost, station, stability material, snap points, colliders
///   meshes.bin   — piece shapes (geometry only, no textures)
///   extract_info.json — which install/version the data came from
/// Nothing from the game ships with the planner: this runs on each player's machine.
/// Coordinates are Unity's (left-handed, Y up, metres), relative to the piece origin.
/// </summary>
public static class GameExtractor
{
    public const int FormatVersion = 1;
    const uint SnapTag = 20000;          // GameObject tag "snappoint"
    static readonly string[] MaterialNames = { "Wood", "Stone", "Iron", "HardWood", "Marble", "Ashstone", "Ancient", "Ice", "Timberwood" };

    public sealed record Info(int Version, string GamePath, string Bundle, long BundleSize, DateTime BundleTime, string Unity, DateTime Extracted, int Pieces, int Shapes);

    // ---------------------------------------------------------------- locating the game
    /// <summary>Valheim folders found through Steam (registry + library folders), best first.</summary>
    public static List<string> FindInstalls()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, key, value) in new[]
            {
                (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try { if (hive.OpenSubKey(key)?.GetValue(value) is string s) steamRoots.Add(s.Replace('/', '\\')); }
                catch (Exception) { /* no access: try the next one */ }
            }
            steamRoots.Add(@"C:\Program Files (x86)\Steam");
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steamRoots.Add(Path.Combine(home, ".steam", "steam"));
            steamRoots.Add(Path.Combine(home, ".local", "share", "Steam"));
        }

        var libraries = new List<string>();
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadAllLines(vdf))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"")) continue;
                var parts = t.Split('"');
                if (parts.Length >= 4) libraries.Add(parts[3].Replace(@"\\", @"\"));
            }
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(l => Path.Combine(l, "steamapps", "common", "Valheim"))
            .Where(IsValheimFolder).ToList();
    }

    public static bool IsValheimFolder(string? dir) =>
        !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "valheim_Data", "StreamingAssets", "SoftRef", "Bundles"));

    /// <summary>The bundle holding Assets/GameElements/Pieces, found through the game's own manifest.</summary>
    public static string FindPieceBundle(string game)
    {
        var softRef = Path.Combine(game, "valheim_Data", "StreamingAssets", "SoftRef");
        var counts = new Dictionary<string, int>();
        string? bundle = null;
        foreach (var line in File.ReadLines(Path.Combine(softRef, "manifest_extended")))
        {
            var t = line.Trim();
            if (t.StartsWith("bundle: ")) bundle = t[8..];
            else if (bundle != null && t.StartsWith("path in bundle: Assets/GameElements/Pieces/") && t.EndsWith(".prefab"))
                counts[bundle] = counts.GetValueOrDefault(bundle) + 1;
        }
        if (counts.Count == 0) throw new InvalidOperationException("No building pieces found in the game manifest");
        return Path.Combine(softRef, "Bundles", counts.OrderByDescending(kv => kv.Value).First().Key);
    }

    public static Info? ReadInfo(string outDir)
    {
        var p = Path.Combine(outDir, "extract_info.json");
        if (!File.Exists(p) || !File.Exists(Path.Combine(outDir, "pieces.json"))) return null;
        try { return JsonSerializer.Deserialize<Info>(File.ReadAllText(p)); }
        catch (Exception) { return null; }
    }

    /// <summary>True when the data on disk no longer matches the installed game (game update, other folder).</summary>
    public static bool IsOutdated(Info info)
    {
        if (info.Version != FormatVersion || !IsValheimFolder(info.GamePath) || !File.Exists(info.Bundle)) return true;
        var fi = new FileInfo(info.Bundle);
        return fi.Length != info.BundleSize || fi.LastWriteTimeUtc != info.BundleTime;
    }

    // ---------------------------------------------------------------- extraction
    /// <param name="progress">(fraction 0..1, step key) — step keys are English UI texts.</param>
    public static Info Run(string game, string outDir, Action<float, string>? progress = null)
    {
        void P(float f, string s) => progress?.Invoke(f, s);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        P(0.02f, "Opening game files…");
        string bundlePath = FindPieceBundle(game);
        var am = new AssetsManager();
        try
        {
            var bun = am.LoadBundleFile(bundlePath, true);
            var afi = am.LoadAssetsFileFromBundle(bun, 0, false);
            var f = afi.file;

            P(0.25f, "Finding the hammer pieces…");
            var scriptKind = new Dictionary<ushort, string>();
            AssetTypeValueField? hammerTable = null;
            AssetTypeValueField GoOf(AssetTypeValueField comp) => am.GetExtAsset(afi, comp["m_GameObject"]).baseField;
            foreach (var a in f.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var si = a.GetScriptIndex(f);
                if (!scriptKind.ContainsKey(si))
                {
                    var names = am.GetBaseField(afi, a).Children.Select(c => c.FieldName).ToHashSet();
                    scriptKind[si] =
                        names.Contains("m_craftingStation") && names.Contains("m_resources") && names.Contains("m_category") ? "Piece" :
                        names.Contains("m_materialType") && names.Contains("m_supports") ? "WearNTear" :
                        names.Contains("m_pieces") && names.Contains("m_categories") ? "PieceTable" : "";
                }
                if (scriptKind[si] == "PieceTable")
                {
                    var bf = am.GetBaseField(afi, a);
                    if (GoOf(bf)["m_Name"].AsString == "_HammerPieceTable") hammerTable = bf;
                }
            }
            if (hammerTable == null) throw new InvalidOperationException("Hammer piece table not found");

            var categoryLabels = hammerTable["m_categoryLabels"]["Array"].Children.Select(c => c.AsString).ToList();
            var categoryIds = hammerTable["m_categories"]["Array"].Children.Select(c => c.AsInt).ToList();

            List<AssetExternal> Components(AssetTypeValueField go) =>
                go["m_Component"]["Array"].Children.Select(c => am.GetExtAsset(afi, c["component"])).ToList();

            var pieces = new List<PieceData>();
            var meshOut = new List<(string, List<MeshExtract.Surface>)>();
            var refs = hammerTable["m_pieces"]["Array"].Children;
            for (int pi = 0; pi < refs.Count; pi++)
            {
                if (pi % 8 == 0) P(0.30f + 0.55f * pi / refs.Count, "Reading pieces and shapes…");
                var rootGo = am.GetExtAsset(afi, refs[pi]).baseField;
                if (rootGo == null) continue;
                var pd = ReadPiece(am, afi, rootGo, Components, GoOf, scriptKind);
                if (pd == null) continue;
                pieces.Add(pd);
                try { meshOut.Add((pd.Prefab, MeshExtract.Extract(am, afi, bun, rootGo))); }
                catch (Exception) { meshOut.Add((pd.Prefab, new List<MeshExtract.Surface>())); }
            }

            P(0.87f, "Reading names in every language…");
            var loc = LoadLocalization(am, Path.Combine(game, "valheim_Data", "resources.assets"));
            Dictionary<string, string>? All(string? token)
            {
                if (token == null || !token.StartsWith('$') || !loc.TryGetValue(token[1..], out var row)) return null;
                var d = row.Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Key != "").ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
                return d.Count > 0 ? d : null;
            }
            foreach (var p in pieces)
            {
                p.Names = All(p.Name);
                p.StationNames = All(p.Station);
                foreach (var r in p.Resources) r.Names = All(r.Token);
            }

            P(0.94f, "Saving…");
            var output = new
            {
                Source = new { Game = "Valheim", Unity = f.Metadata.UnityVersion, Extracted = DateTime.UtcNow.ToString("u") },
                CoordinateSystem = "Unity: left-handed, +Y up, +Z forward, metres, relative to piece origin",
                Categories = categoryIds.Zip(categoryLabels, (id, label) => new { Id = id, Label = label, Names = All(label) }),
                Pieces = pieces,
            };
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "pieces.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
            MeshExtract.Write(Path.Combine(outDir, "meshes.bin"), meshOut);

            var bi = new FileInfo(bundlePath);
            var info = new Info(FormatVersion, game, bundlePath, bi.Length, bi.LastWriteTimeUtc, f.Metadata.UnityVersion, DateTime.UtcNow,
                pieces.Count, meshOut.Count(m => m.Item2.Count > 0));
            File.WriteAllText(Path.Combine(outDir, "extract_info.json"), JsonSerializer.Serialize(info));
            P(1f, "Done");
            return info;
        }
        finally
        {
            am.UnloadAll(true);
            GC.Collect();
        }
    }

    static PieceData? ReadPiece(AssetsManager am, AssetsFileInstance afi, AssetTypeValueField rootGo,
        Func<AssetTypeValueField, List<AssetExternal>> Components, Func<AssetTypeValueField, AssetTypeValueField> GoOf,
        Dictionary<ushort, string> scriptKind)
    {
        static Vector3 V3(AssetTypeValueField v) => new(v["x"].AsFloat, v["y"].AsFloat, v["z"].AsFloat);
        static Quaternion Q(AssetTypeValueField v) => new(v["x"].AsFloat, v["y"].AsFloat, v["z"].AsFloat, v["w"].AsFloat);
        static float R(float x) => MathF.Round(x, 4) + 0f;
        static float[] A(Vector3 v) => new[] { R(v.X), R(v.Y), R(v.Z) };
        static float[] AQ(Quaternion q) => new[] { R(q.X), R(q.Y), R(q.Z), R(q.W) };

        AssetTypeValueField? piece = null, wnt = null;
        foreach (var c in Components(rootGo).Where(c => c.info?.TypeId == 114))
        {
            var k = scriptKind.GetValueOrDefault(c.info!.GetScriptIndex(c.file.file), "");
            if (k == "")
            {
                var n = c.baseField.Children.Select(x => x.FieldName).ToHashSet();
                k = n.Contains("m_craftingStation") && n.Contains("m_resources") ? "Piece" : n.Contains("m_materialType") && n.Contains("m_supports") ? "WearNTear" : "";
            }
            if (k == "Piece") piece = c.baseField; else if (k == "WearNTear") wnt = c.baseField;
        }
        if (piece == null) return null;

        var pd = new PieceData
        {
            Prefab = rootGo["m_Name"].AsString,
            Name = piece["m_name"].AsString,
            Category = piece["m_category"].AsInt,
            Enabled = piece["m_enabled"].AsBool,
            GroundPiece = piece["m_groundPiece"].AsBool,
            GroundOnly = piece["m_groundOnly"].AsBool,
            CanRotate = piece["m_canRotate"].AsBool,
            Comfort = piece["m_comfort"].AsInt,
        };
        pd.Station = am.GetExtAsset(afi, piece["m_craftingStation"]).baseField?["m_name"].AsString;
        foreach (var r in piece["m_resources"]["Array"].Children)
        {
            var item = am.GetExtAsset(afi, r["m_resItem"]).baseField;
            pd.Resources.Add(new ResourceData(item != null ? GoOf(item)["m_Name"].AsString : "?", r["m_amount"].AsInt, r["m_recover"].AsBool)
            { Token = item?["m_itemData"]["m_shared"]["m_name"].AsString ?? "" });
        }
        if (wnt != null)
        {
            int mt = wnt["m_materialType"].AsInt;
            pd.Stability = new StabilityData(mt < MaterialNames.Length ? MaterialNames[mt] : mt.ToString(CultureInfo.InvariantCulture),
                wnt["m_supports"].AsBool, A(V3(wnt["m_comOffset"])), R(wnt["m_health"].AsFloat), wnt["m_forceCorrectCOMCalculation"].AsBool);
        }

        // Walk hierarchy below the root; the root's own transform is replaced by the placement transform.
        void Walk(AssetTypeValueField go, Matrix4x4 parent, Quaternion parentRot, Vector3 parentScale, bool isRoot, bool parentActive)
        {
            bool active = parentActive && go["m_IsActive"].AsBool;
            var cs = Components(go);
            var tr = cs.First(c => c.info!.TypeId is 4 or 224).baseField;
            Matrix4x4 world; Quaternion rot; Vector3 scale;
            if (isRoot) { world = Matrix4x4.Identity; rot = Quaternion.Identity; scale = Vector3.One; }
            else
            {
                var lp = V3(tr["m_LocalPosition"]); var lq = Q(tr["m_LocalRotation"]); var ls = V3(tr["m_LocalScale"]);
                world = Matrix4x4.CreateScale(ls) * Matrix4x4.CreateFromQuaternion(lq) * Matrix4x4.CreateTranslation(lp) * parent;
                rot = parentRot * lq; scale = parentScale * ls;
            }
            if (!isRoot && go["m_Tag"].AsUInt == SnapTag)
                pd.SnapPoints.Add(new SnapData(go["m_Name"].AsString, A(Vector3.Transform(Vector3.Zero, world))));

            foreach (var c in cs)
            {
                var b = c.baseField; int t = c.info!.TypeId;
                if (t is not (65 or 64 or 135 or 136) || b["m_IsTrigger"].AsBool) continue;
                ColliderData cd;
                if (t == 65)
                    cd = new ColliderData("box", A(Vector3.Transform(V3(b["m_Center"]), world)), A(V3(b["m_Size"]) * scale), AQ(rot));
                else if (t == 64)
                {
                    var mesh = am.GetExtAsset(afi, b["m_Mesh"]).baseField;
                    if (mesh == null) continue;
                    var aabb = mesh["m_LocalAABB"];
                    cd = new ColliderData("mesh", A(Vector3.Transform(V3(aabb["m_Center"]), world)), A(V3(aabb["m_Extent"]) * 2 * scale), AQ(rot));
                }
                else if (t == 135)
                {
                    float rad = b["m_Radius"].AsFloat * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                    cd = new ColliderData("sphere", A(Vector3.Transform(V3(b["m_Center"]), world)), new[] { R(rad * 2), R(rad * 2), R(rad * 2) }, AQ(rot));
                }
                else
                {
                    int dir = b["m_Direction"].AsInt; float rad = b["m_Radius"].AsFloat, h = b["m_Height"].AsFloat;
                    var size = new Vector3(rad * 2, rad * 2, rad * 2); if (dir == 0) size.X = h; else if (dir == 1) size.Y = h; else size.Z = h;
                    cd = new ColliderData("capsule", A(Vector3.Transform(V3(b["m_Center"]), world)), A(size * scale), AQ(rot));
                }
                cd.Active = active && b["m_Enabled"].AsBool;
                pd.Colliders.Add(cd);
            }
            foreach (var ch in tr["m_Children"]["Array"].Children)
            {
                var ct = am.GetExtAsset(afi, ch).baseField;
                Walk(am.GetExtAsset(afi, ct["m_GameObject"]).baseField, world, rot, scale, false, active);
            }
        }
        Walk(rootGo, Matrix4x4.Identity, Quaternion.Identity, Vector3.One, true, true);
        return pd;
    }

    static Dictionary<string, Dictionary<string, string>> LoadLocalization(AssetsManager am, string resourcesPath)
    {
        var res = new Dictionary<string, Dictionary<string, string>>();
        var ra = am.LoadAssetsFile(resourcesPath, false);
        foreach (var ta in ra.file.GetAssetsOfType(AssetClassID.TextAsset))
        {
            // resources.assets has no type tree; TextAsset layout is: string m_Name (4-aligned), string m_Script.
            var r = ra.file.Reader; r.Position = ta.GetAbsoluteByteOffset(ra.file);
            int nlen = r.ReadInt32(); r.Position += (nlen + 3) & ~3;
            int slen = r.ReadInt32();
            if (slen <= 0 || slen > ta.ByteSize) continue;
            var text = System.Text.Encoding.UTF8.GetString(r.ReadBytes(slen));
            if (!text.Contains("\"piece_woodwall")) continue;
            var rows = ParseCsv(text);
            var header = rows[0];
            foreach (var row in rows.Skip(1))
            {
                if (row.Count == 0 || row[0] == "") continue;
                var d = new Dictionary<string, string>();
                for (int i = 1; i < Math.Min(row.Count, header.Count); i++) d[header[i]] = row[i];
                res[row[0]] = d;
            }
        }
        return res;
    }

    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (q) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); }
            else if (c != '\r') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    sealed class PieceData
    {
        public string Prefab { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, string>? Names { get; set; }
        public Dictionary<string, string>? StationNames { get; set; }
        public int Category { get; set; }
        public bool Enabled { get; set; }
        [JsonIgnore] public string? Station { get; set; }
        public List<ResourceData> Resources { get; } = new();
        public StabilityData? Stability { get; set; }
        public List<SnapData> SnapPoints { get; } = new();
        public List<ColliderData> Colliders { get; } = new();
        public bool GroundPiece { get; set; }
        public bool GroundOnly { get; set; }
        public bool CanRotate { get; set; }
        public int Comfort { get; set; }
    }
    sealed record ResourceData(string Item, int Amount, bool Recover)
    {
        [JsonIgnore] public string Token { get; set; } = "";
        public Dictionary<string, string>? Names { get; set; }
    }
    sealed record StabilityData(string Material, bool Supports, float[] ComOffset, float Health, bool ForceCorrectCom);
    sealed record SnapData(string Name, float[] Pos);
    sealed record ColliderData(string Shape, float[] Center, float[] Size, float[] Rotation)
    {
        public bool Active { get; set; }
    }
}
