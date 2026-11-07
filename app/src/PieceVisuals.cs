using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ValheimPlanner;

/// <summary>Builds simple block geometry for a piece from its collision boxes (no game art is used).</summary>
public static class PieceVisuals
{
    static readonly Dictionary<string, Color> MaterialColors = new()
    {
        ["Wood"] = new Color(0.62f, 0.43f, 0.25f),
        ["HardWood"] = new Color(0.45f, 0.30f, 0.18f),
        ["Timberwood"] = new Color(0.55f, 0.36f, 0.22f),
        ["Stone"] = new Color(0.58f, 0.58f, 0.56f),
        ["Marble"] = new Color(0.86f, 0.85f, 0.82f),
        ["Ashstone"] = new Color(0.30f, 0.29f, 0.30f),
        ["Iron"] = new Color(0.33f, 0.36f, 0.42f),
        ["Ancient"] = new Color(0.40f, 0.50f, 0.36f),
        ["Ice"] = new Color(0.70f, 0.85f, 0.95f),
    };
    static readonly Dictionary<string, StandardMaterial3D> SolidMats = new();
    static StandardMaterial3D? _ghostMat, _ghostSnapMat;

    public static Color ColorFor(string? material) =>
        material != null && MaterialColors.TryGetValue(material, out var c) ? c : new Color(0.6f, 0.45f, 0.7f);

    public static StandardMaterial3D SolidMaterial(string? material)
    {
        string key = material ?? "";
        if (!SolidMats.TryGetValue(key, out var m))
            SolidMats[key] = m = new StandardMaterial3D { AlbedoColor = ColorFor(material), Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        return m;
    }

    public static StandardMaterial3D GhostMaterial(bool snapped)
    {
        _ghostMat ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 1f, 1f, 0.45f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _ghostSnapMat ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.4f, 1f, 0.5f, 0.5f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        return snapped ? _ghostSnapMat : _ghostMat;
    }

    /// <summary>Solid, pickable piece: a StaticBody3D with one box mesh + shape per collider.</summary>
    public static StaticBody3D BuildSolid(PieceDef def)
    {
        var body = new StaticBody3D { Name = def.Prefab };
        var mat = PieceShader.MaterialFor(def);
        var real = MeshLibrary.Get(def);
        if (real != null)
        {
            // Real shape from the player's game files; each surface carries its own material.
            var mi = new MeshInstance3D { Mesh = real };
            mi.SetMeta("own", true);
            mi.SetInstanceShaderParameter("box_size", real.GetAabb().Size);
            mi.SetInstanceShaderParameter("edge_amt", 0f);
            body.AddChild(mi);
        }
        foreach (var b in def.Boxes)
        {
            var xf = new Transform3D(new Basis(b.Rotation), b.Center);
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = b.Size.Max(new Vector3(0.02f, 0.02f, 0.02f)) }, Transform = xf });
            if (real != null) continue;
            // Shrink slightly so seams between neighbouring pieces stay visible.
            var visSize = (b.Size - new Vector3(0.02f, 0.02f, 0.02f)).Max(new Vector3(0.01f, 0.01f, 0.01f));
            var bi = new MeshInstance3D { Mesh = new BoxMesh { Size = visSize }, Transform = xf, MaterialOverride = mat };
            bi.SetInstanceShaderParameter("box_size", visSize);
            body.AddChild(bi);
        }
        if (real != null) return body;
        if (def.Triangle != null)
        {
            var (mesh, pts) = Prism(def.Triangle, 0.24f);
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
            var aabb = mesh.GetAabb();
            mi.SetInstanceShaderParameter("box_size", aabb.Size);
            mi.SetInstanceShaderParameter("edge_amt", 0f);
            body.AddChild(mi);
            body.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = pts } });
        }
        return body;
    }

    /// <summary>Triangular slab (gable wall fill) of the given thickness along local Z.</summary>
    static (ArrayMesh, Vector3[]) Prism(Vector3[] tri, float thickness)
    {
        var dz = new Vector3(0, 0, thickness / 2);
        var f = tri.Select(p => p + dz).ToArray();
        var b = tri.Select(p => p - dz).ToArray();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Tri(Vector3 a, Vector3 c, Vector3 d) { st.AddVertex(a); st.AddVertex(c); st.AddVertex(d); }
        Tri(f[0], f[1], f[2]);
        Tri(b[2], b[1], b[0]);
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            Tri(f[i], b[i], b[j]);
            Tri(f[i], b[j], f[j]);
        }
        st.GenerateNormals();
        return (st.Commit(), f.Concat(b).ToArray());
    }

    /// <summary>Translucent preview without collision.</summary>
    public static Node3D BuildGhost(PieceDef def, out List<MeshInstance3D> meshes)
    {
        var root = new Node3D { Name = "Ghost" };
        meshes = new List<MeshInstance3D>();
        var real = MeshLibrary.Get(def);
        if (real != null)
        {
            var mi = new MeshInstance3D { Mesh = real, MaterialOverride = GhostMaterial(false), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            root.AddChild(mi);
            meshes.Add(mi);
        }
        foreach (var b in def.Boxes)
        {
            if (real != null) break;
            var mi = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = b.Size.Max(new Vector3(0.02f, 0.02f, 0.02f)) },
                Transform = new Transform3D(new Basis(b.Rotation), b.Center),
                MaterialOverride = GhostMaterial(false),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            root.AddChild(mi);
            meshes.Add(mi);
        }
        if (def.Triangle != null && real == null)
        {
            var mi = new MeshInstance3D { Mesh = Prism(def.Triangle, 0.24f).Item1, MaterialOverride = GhostMaterial(false), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            root.AddChild(mi);
            meshes.Add(mi);
        }
        // Small markers on snap points help reading how the piece will attach.
        var dotMesh = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 8, Rings = 4 };
        var dotMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true };
        foreach (var s in def.Snaps)
            root.AddChild(new MeshInstance3D { Mesh = dotMesh, Position = s, MaterialOverride = dotMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        return root;
    }
}
