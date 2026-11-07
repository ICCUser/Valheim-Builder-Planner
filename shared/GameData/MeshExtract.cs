using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

/// <summary>
/// Reads the visible shape of a piece (the "New" state, best affordable LOD) from the player's own game files.
/// Output stays on the player's machine; only geometry is kept (no textures), in Unity coordinates.
/// </summary>
namespace ValheimPlanner.GameData;

public static class MeshExtract
{
    public sealed class Surface
    {
        public byte Kind = 255;                 // 255 = use the piece's default look
        public List<Vector3> Pos = new();
        public List<Vector3> Nrm = new();
        public List<int> Idx = new();
    }

    const int MaxVerticesPerLod = 9000;

    public static List<Surface> Extract(AssetsManager am, AssetsFileInstance afi, BundleFileInstance bun, AssetTypeValueField root)
    {
        var banned = new HashSet<long>();
        var chosen = new HashSet<long>();
        var bySurfaceKind = new Dictionary<byte, Surface>();

        List<AssetExternal> Comps(AssetTypeValueField go) =>
            go["m_Component"]["Array"].Children.Select(c => am.GetExtAsset(afi, c["component"])).ToList();

        int MeshVerts(AssetExternal renderer)
        {
            var go = am.GetExtAsset(afi, renderer.baseField["m_GameObject"]).baseField;
            var mf = Comps(go).FirstOrDefault(c => c.info?.TypeId == 33);
            if (mf.baseField == null) return -1;
            if (Builtin(afi, mf.baseField["m_Mesh"]) is { } prim) return prim.Pos.Count;
            var mesh = am.GetExtAsset(afi, mf.baseField["m_Mesh"]).baseField;
            return mesh == null ? -1 : (int)mesh["m_VertexData"]["m_VertexCount"].AsUInt;
        }

        void Walk(AssetTypeValueField go, Matrix4x4 parent, bool isRoot, bool parentActive)
        {
            bool active = parentActive && go["m_IsActive"].AsBool;
            string name = go["m_Name"].AsString.ToLowerInvariant();
            if (!active || (!isRoot && IsSnowNode(name)) || name == "worn" || name == "broken" || name.Contains("destruction") || name == "wet") return;
            var comps = Comps(go);
            var tr = comps.First(c => c.info!.TypeId is 4 or 224).baseField;
            Matrix4x4 world = Matrix4x4.Identity;
            if (!isRoot)
            {
                var p = tr["m_LocalPosition"]; var q = tr["m_LocalRotation"]; var s = tr["m_LocalScale"];
                world = Matrix4x4.CreateScale(s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat)
                      * Matrix4x4.CreateFromQuaternion(new Quaternion(q["x"].AsFloat, q["y"].AsFloat, q["z"].AsFloat, q["w"].AsFloat))
                      * Matrix4x4.CreateTranslation(p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat) * parent;
            }

            // LOD group: keep the most detailed level that resolves and stays under the vertex budget.
            foreach (var lg in comps.Where(c => c.info?.TypeId == 205))
            {
                var levels = lg.baseField["m_LODs"]["Array"].Children
                    .Select(l => l["renderers"]["Array"].Children.Select(r => r["renderer"]).ToList()).ToList();
                int pick = -1;
                for (int i = 0; i < levels.Count && pick < 0; i++)
                {
                    int total = 0; bool ok = levels[i].Count > 0;
                    foreach (var r in levels[i])
                    {
                        if (r["m_PathID"].AsLong == 0) continue;          // empty LOD slot
                        var re = am.GetExtAsset(afi, r);
                        if (re.baseField == null || re.info?.TypeId is not (23 or 137)) continue;   // particles, lights… have no shape
                        int v = re.info.TypeId == 137
                            ? (int)(am.GetExtAsset(afi, re.baseField["m_Mesh"]).baseField?["m_VertexData"]["m_VertexCount"].AsUInt ?? 0)
                            : MeshVerts(re);
                        if (v < 0) continue;
                        total += v;
                    }
                    if (ok && (total <= MaxVerticesPerLod || i == levels.Count - 1)) pick = i;
                }
                for (int i = 0; i < levels.Count; i++)
                    foreach (var r in levels[i])
                        (i == pick ? chosen : banned).Add(r["m_PathID"].AsLong);
            }

            // Animated parts (SkinnedMeshRenderer): use the mesh in its rest pose.
            var skinned = comps.FirstOrDefault(c => c.info?.TypeId == 137);
            if (skinned.baseField != null && skinned.baseField["m_Enabled"].AsBool
                && (!banned.Contains(skinned.info!.PathId) || chosen.Contains(skinned.info.PathId)))
            {
                var mats = skinned.baseField["m_Materials"]["Array"].Children
                    .Select(m => am.GetExtAsset(afi, m).baseField?["m_Name"].AsString ?? "").ToList();
                if (!mats.Any(m => IsSnowMat(m)) && am.GetExtAsset(afi, skinned.baseField["m_Mesh"]).baseField is { } smesh)
                    AddMesh(smesh, mats, world, bySurfaceKind, bun);
            }

            var renderer = comps.FirstOrDefault(c => c.info?.TypeId == 23);
            var filter = comps.FirstOrDefault(c => c.info?.TypeId == 33);
            if (renderer.baseField != null && filter.baseField != null && renderer.baseField["m_Enabled"].AsBool
                && (!banned.Contains(renderer.info!.PathId) || chosen.Contains(renderer.info.PathId)))
            {
                var mats = renderer.baseField["m_Materials"]["Array"].Children
                    .Select(m => am.GetExtAsset(afi, m).baseField?["m_Name"].AsString ?? "").ToList();
                if (!mats.Any(m => IsSnowMat(m)))
                {
                    if (Builtin(afi, filter.baseField["m_Mesh"]) is { } prim)
                        AddPrimitive(prim, mats.FirstOrDefault() ?? "", world, bySurfaceKind);
                    else if (am.GetExtAsset(afi, filter.baseField["m_Mesh"]).baseField is { } mesh)
                        AddMesh(mesh, mats, world, bySurfaceKind, bun);
                }
            }

            foreach (var ch in tr["m_Children"]["Array"].Children)
            {
                var ct = am.GetExtAsset(afi, ch).baseField;
                Walk(am.GetExtAsset(afi, ct["m_GameObject"]).baseField, world, false, active);
            }
        }

        Walk(root, Matrix4x4.Identity, true, true);
        return bySurfaceKind.Values.Where(s => s.Idx.Count > 0).ToList();
    }

    // Snow cover layers that the game shows only in winter (e.g. "floor_2x2_snow", "Snow2", material "snow_flat").
    static bool IsSnowNode(string n) => n.EndsWith("_snow") || n.StartsWith("snow") && !n.Contains("lantern");
    static bool IsSnowMat(string m) { m = m.ToLowerInvariant(); return m == "snow" || m.StartsWith("snow_flat") || m.StartsWith("snow_cover"); }

    /// <summary>Material name → look. 255 = piece default.</summary>
    public static byte KindOf(string material)
    {
        string m = material.ToLowerInvariant();
        bool Has(params string[] k) => k.Any(m.Contains);
        if (Has("thatch", "straw", "hay")) return 4;
        if (Has("marble")) return 5;
        if (Has("stone", "rock", "grausten", "brick", "flint")) return 2;
        if (Has("iron", "metal", "bronze", "copper", "anvil", "chain", "nail", "blackmetal", "flametal", "silver")) return 3;
        if (Has("cloth", "banner", "rug", "fabric", "hide", "leather", "pelt", "fur", "linen", "carpet", "curtain", "jute")) return 7;
        if (Has("glass", "ice", "crystal")) return 6;
        if (Has("log", "bark", "corewood", "trunk")) return 1;
        if (Has("wood", "plank", "board", "darkwood", "ashwood", "beam")) return 0;
        return 255;
    }

    static void AddMesh(AssetTypeValueField mesh, List<string> mats, Matrix4x4 world, Dictionary<byte, Surface> outSurfaces, BundleFileInstance bun)
    {
        if (mesh["m_MeshCompression"].AsInt != 0) return;
        var vd = mesh["m_VertexData"];
        int vc = (int)vd["m_VertexCount"].AsUInt;
        byte[] data = vd["m_DataSize"].AsByteArray;
        if (data.Length == 0 && mesh["m_StreamData"]["size"].AsUInt > 0)
            data = ReadStream(bun, mesh["m_StreamData"]["path"].AsString, (long)mesh["m_StreamData"]["offset"].AsULong, (int)mesh["m_StreamData"]["size"].AsUInt);
        if (vc == 0 || data.Length == 0) return;

        var channels = vd["m_Channels"]["Array"].Children
            .Select(c => (Stream: c["stream"].AsByte, Offset: c["offset"].AsByte, Format: c["format"].AsByte, Dim: (byte)(c["dimension"].AsByte & 0xF))).ToList();
        int streams = channels.Max(c => c.Stream) + 1;
        var streamOff = new int[streams]; var stride = new int[streams];
        int off = 0;
        for (int s = 0; s < streams; s++)
        {
            streamOff[s] = off;
            stride[s] = channels.Where(c => c.Stream == s && c.Dim > 0).Sum(c => FormatSize(c.Format) * c.Dim);
            off += vc * stride[s];
            off = (off + 15) & ~15;
        }

        Vector3 Read3(int ch, int v)
        {
            var c = channels[ch];
            int o = streamOff[c.Stream] + v * stride[c.Stream] + c.Offset;
            int sz = FormatSize(c.Format);
            float R(int i) => ReadComp(data, o + i * sz, c.Format);
            return new Vector3(R(0), R(1), c.Dim > 2 ? R(2) : 0);
        }

        bool hasNormal = channels.Count > 1 && channels[1].Dim >= 3;
        Matrix4x4.Invert(world, out var inv);
        var nrmMat = Matrix4x4.Transpose(inv);
        var pos = new Vector3[vc]; var nrm = new Vector3[vc];
        for (int v = 0; v < vc; v++)
        {
            pos[v] = Vector3.Transform(Read3(0, v), world);
            var n = hasNormal ? Vector3.TransformNormal(Read3(1, v), nrmMat) : Vector3.UnitY;
            nrm[v] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
        }

        byte[] ib = mesh["m_IndexBuffer"]["Array"].AsByteArray;
        bool i32 = mesh["m_IndexFormat"].AsInt == 1;
        var subs = mesh["m_SubMeshes"]["Array"].Children;
        for (int si = 0; si < subs.Count; si++)
        {
            var sm = subs[si];
            if (sm["topology"].AsInt != 0) continue;              // triangles only
            string matName = mats.Count == 0 ? "" : mats[Math.Min(si, mats.Count - 1)];
            if (IsSnowMat(matName)) continue;
            byte kind = KindOf(matName);
            if (!outSurfaces.TryGetValue(kind, out var surf)) outSurfaces[kind] = surf = new Surface { Kind = kind };
            int first = (int)sm["firstByte"].AsUInt, count = (int)sm["indexCount"].AsUInt, baseV = sm["baseVertex"].AsInt;
            var map = new Dictionary<int, int>();
            for (int k = 0; k < count; k++)
            {
                int raw = i32 ? BitConverter.ToInt32(ib, first + k * 4) : BitConverter.ToUInt16(ib, first + k * 2);
                int vi = raw + baseV;
                if ((uint)vi >= (uint)vc) { vi = 0; }
                if (!map.TryGetValue(vi, out int ni))
                {
                    ni = surf.Pos.Count;
                    surf.Pos.Add(pos[vi]); surf.Nrm.Add(nrm[vi]);
                    map[vi] = ni;
                }
                surf.Idx.Add(ni);
            }
        }
    }

    // ---- Unity built-in primitives ("unity default resources"), regenerated procedurally ----
    static readonly Dictionary<long, Surface> Primitives = new()
    {
        [10202] = Box(),                     // Cube 1×1×1
        [10210] = QuadMesh(1f, false),       // Quad 1×1 in XY
        [10209] = QuadMesh(10f, true),       // Plane 10×10 in XZ
        [10206] = Cylinder(0.5f, 2f, 16),    // Cylinder r 0.5, h 2
        [10207] = Cylinder(0.5f, 1f, 12),    // Sphere (approximated by a short cylinder)
        [10208] = Cylinder(0.5f, 2f, 12),    // Capsule (approximated)
    };

    static Surface? Builtin(AssetsFileInstance afi, AssetTypeValueField pptr)
    {
        int fid = pptr["m_FileID"].AsInt;
        if (fid <= 0) return null;
        var ext = afi.file.Metadata.Externals[fid - 1].PathName;
        if (!ext.Contains("unity default resources")) return null;
        return Primitives.GetValueOrDefault(pptr["m_PathID"].AsLong);
    }

    static void AddPrimitive(Surface prim, string mat, Matrix4x4 world, Dictionary<byte, Surface> outSurfaces)
    {
        byte kind = KindOf(mat);
        if (!outSurfaces.TryGetValue(kind, out var surf)) outSurfaces[kind] = surf = new Surface { Kind = kind };
        Matrix4x4.Invert(world, out var inv);
        var nm = Matrix4x4.Transpose(inv);
        int b = surf.Pos.Count;
        for (int i = 0; i < prim.Pos.Count; i++)
        {
            surf.Pos.Add(Vector3.Transform(prim.Pos[i], world));
            var n = Vector3.TransformNormal(prim.Nrm[i], nm);
            surf.Nrm.Add(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY);
        }
        foreach (var i in prim.Idx) surf.Idx.Add(b + i);
    }

    // Unity winding: clockwise when seen from the front (left-handed).
    static void Quad(Surface s, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        int o = s.Pos.Count;
        s.Pos.AddRange(new[] { a, b, c, d }); for (int i = 0; i < 4; i++) s.Nrm.Add(n);
        s.Idx.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3 });
    }

    static Surface Box()
    {
        var s = new Surface(); float h = 0.5f;
        Vector3 V(float x, float y, float z) => new(x * h, y * h, z * h);
        Quad(s, V(-1, -1, 1), V(-1, 1, 1), V(1, 1, 1), V(1, -1, 1), Vector3.UnitZ);
        Quad(s, V(1, -1, -1), V(1, 1, -1), V(-1, 1, -1), V(-1, -1, -1), -Vector3.UnitZ);
        Quad(s, V(1, -1, 1), V(1, 1, 1), V(1, 1, -1), V(1, -1, -1), Vector3.UnitX);
        Quad(s, V(-1, -1, -1), V(-1, 1, -1), V(-1, 1, 1), V(-1, -1, 1), -Vector3.UnitX);
        Quad(s, V(-1, 1, -1), V(1, 1, -1), V(1, 1, 1), V(-1, 1, 1), Vector3.UnitY);
        Quad(s, V(-1, -1, 1), V(1, -1, 1), V(1, -1, -1), V(-1, -1, -1), -Vector3.UnitY);
        return s;
    }

    static Surface QuadMesh(float size, bool horizontal)
    {
        var s = new Surface(); float h = size / 2;
        if (horizontal) Quad(s, new(-h, 0, -h), new(-h, 0, h), new(h, 0, h), new(h, 0, -h), Vector3.UnitY);
        else Quad(s, new(-h, -h, 0), new(-h, h, 0), new(h, h, 0), new(h, -h, 0), -Vector3.UnitZ);
        return s;
    }

    static Surface Cylinder(float r, float height, int seg)
    {
        var s = new Surface(); float hy = height / 2;
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var p0 = new Vector3(MathF.Cos(a0) * r, 0, MathF.Sin(a0) * r);
            var p1 = new Vector3(MathF.Cos(a1) * r, 0, MathF.Sin(a1) * r);
            var n = Vector3.Normalize(new Vector3(MathF.Cos((a0 + a1) / 2), 0, MathF.Sin((a0 + a1) / 2)));
            Quad(s, p1 + new Vector3(0, -hy, 0), p1 + new Vector3(0, hy, 0), p0 + new Vector3(0, hy, 0), p0 + new Vector3(0, -hy, 0), n);
            int o = s.Pos.Count;
            s.Pos.AddRange(new[] { new Vector3(0, hy, 0), p0 + new Vector3(0, hy, 0), p1 + new Vector3(0, hy, 0) });
            s.Pos.AddRange(new[] { new Vector3(0, -hy, 0), p1 + new Vector3(0, -hy, 0), p0 + new Vector3(0, -hy, 0) });
            for (int k = 0; k < 3; k++) s.Nrm.Add(Vector3.UnitY);
            for (int k = 0; k < 3; k++) s.Nrm.Add(-Vector3.UnitY);
            s.Idx.AddRange(new[] { o, o + 1, o + 2, o + 3, o + 4, o + 5 });
        }
        return s;
    }

    /// <summary>Vertex data kept in the bundle's .resS block.</summary>
    static byte[] ReadStream(BundleFileInstance bun, string path, long offset, int size)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        var dir = bun.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(d => d.Name == name);
        if (dir == null) return Array.Empty<byte>();
        lock (bun)
        {
            var r = bun.file.DataReader;
            r.Position = dir.Offset + offset;
            return r.ReadBytes(size);
        }
    }

    static int FormatSize(byte f) => f switch { 0 => 4, 1 => 2, 2 or 3 or 6 or 7 => 1, 4 or 5 or 8 or 9 => 2, _ => 4 };

    static float ReadComp(byte[] d, int o, byte f) => f switch
    {
        0 => BitConverter.ToSingle(d, o),
        1 => (float)BitConverter.ToHalf(d, o),
        2 => d[o] / 255f,
        3 => Math.Max((sbyte)d[o] / 127f, -1f),
        4 => BitConverter.ToUInt16(d, o) / 65535f,
        5 => Math.Max(BitConverter.ToInt16(d, o) / 32767f, -1f),
        6 => d[o], 7 => (sbyte)d[o],
        8 => BitConverter.ToUInt16(d, o), 9 => BitConverter.ToInt16(d, o),
        10 => BitConverter.ToUInt32(d, o), _ => BitConverter.ToInt32(d, o),
    };

    /// <summary>Binary layout: "VPM1", int count, then per piece: string prefab, int surfaces,
    /// per surface: byte kind, int vcount, float3 positions, sbyte3 normals, int icount, int indices.</summary>
    public static void Write(string path, List<(string Prefab, List<Surface> Surfaces)> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("VPM1"u8.ToArray());
        w.Write(all.Count);
        foreach (var (prefab, surfs) in all)
        {
            w.Write(prefab);
            w.Write(surfs.Count);
            foreach (var s in surfs)
            {
                w.Write(s.Kind);
                w.Write(s.Pos.Count);
                foreach (var p in s.Pos) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                foreach (var n in s.Nrm) { w.Write((sbyte)Math.Round(n.X * 127)); w.Write((sbyte)Math.Round(n.Y * 127)); w.Write((sbyte)Math.Round(n.Z * 127)); }
                w.Write(s.Idx.Count);
                foreach (var i in s.Idx) w.Write(i);
            }
        }
    }
}
