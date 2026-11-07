using System.Collections.Generic;
using System.IO;
using Godot;

namespace ValheimPlanner;

/// <summary>
/// Real piece shapes extracted by tools/Extractor from the player's own game install into user://meshes.bin.
/// The file never ships with the app; without it pieces fall back to their collision boxes.
/// </summary>
public static class MeshLibrary
{
    const string FilePath = "user://meshes.bin";

    sealed record RawSurface(byte Kind, Vector3[] Pos, Vector3[] Nrm, int[] Idx);

    static Dictionary<string, List<RawSurface>>? _raw;
    static readonly Dictionary<string, ArrayMesh?> Built = new();

    public static int Count => _raw?.Count ?? 0;

    public static void Load()
    {
        _raw = new();
        var abs = ProjectSettings.GlobalizePath(FilePath);
        if (!File.Exists(abs)) { GD.Print("No meshes.bin: pieces use simple blocks"); return; }
        using var r = new BinaryReader(File.OpenRead(abs));
        if (new string(r.ReadChars(4)) != "VPM1") { GD.PushWarning("meshes.bin: unknown format"); return; }
        int n = r.ReadInt32();
        for (int p = 0; p < n; p++)
        {
            string prefab = r.ReadString();
            int sc = r.ReadInt32();
            var list = new List<RawSurface>(sc);
            for (int s = 0; s < sc; s++)
            {
                byte kind = r.ReadByte();
                int vc = r.ReadInt32();
                var pos = new Vector3[vc]; var nrm = new Vector3[vc];
                // Unity (left-handed) -> Godot (right-handed): mirror Z.
                for (int i = 0; i < vc; i++) pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), -r.ReadSingle());
                for (int i = 0; i < vc; i++) nrm[i] = new Vector3(r.ReadSByte() / 127f, r.ReadSByte() / 127f, -r.ReadSByte() / 127f).Normalized();
                int ic = r.ReadInt32();
                var idx = new int[ic];
                for (int i = 0; i < ic; i++) idx[i] = r.ReadInt32();
                // Mirroring Z flips handedness, which already turns Unity's clockwise fronts into the
                // orientation Godot expects here: the index order is kept as is.
                if (vc > 0 && ic > 0) list.Add(new RawSurface(kind, pos, nrm, idx));
            }
            if (list.Count > 0) _raw[prefab] = list;
        }
        GD.Print($"Loaded real shapes for {_raw.Count} pieces");
    }

    /// <summary>Mesh with one surface per look (wood, metal, cloth…), materials already assigned.</summary>
    public static ArrayMesh? Get(PieceDef def)
    {
        if (_raw == null || !_raw.TryGetValue(def.Prefab, out var surfs)) return null;
        if (Built.TryGetValue(def.Prefab, out var m)) return m;
        var mesh = new ArrayMesh();
        foreach (var s in surfs)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = s.Pos;
            arrays[(int)Mesh.ArrayType.Normal] = s.Nrm;
            arrays[(int)Mesh.ArrayType.Index] = s.Idx;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1,
                s.Kind == 255 ? PieceShader.MaterialFor(def) : PieceShader.MaterialFor((PieceShader.Kind)s.Kind, PieceShader.DefaultColor((PieceShader.Kind)s.Kind, def)));
        }
        Built[def.Prefab] = mesh;
        return mesh;
    }
}
