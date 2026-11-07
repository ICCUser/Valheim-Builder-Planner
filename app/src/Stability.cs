using System;
using System.Collections.Generic;
using Godot;

namespace ValheimPlanner;

/// <summary>Oriented box in world space.</summary>
public readonly record struct Obb(Vector3 Center, Basis Axes, Vector3 Half, bool IsMesh = false)
{
    public Vector3 ClosestPoint(Vector3 q)
    {
        var local = Axes.Transposed() * (q - Center);
        return Center + Axes * local.Clamp(-Half, Half);
    }

    public (Vector3 min, Vector3 max) Aabb()
    {
        var e = new Vector3(
            Mathf.Abs(Axes.X.X) * Half.X + Mathf.Abs(Axes.Y.X) * Half.Y + Mathf.Abs(Axes.Z.X) * Half.Z,
            Mathf.Abs(Axes.X.Y) * Half.X + Mathf.Abs(Axes.Y.Y) * Half.Y + Mathf.Abs(Axes.Z.Y) * Half.Z,
            Mathf.Abs(Axes.X.Z) * Half.X + Mathf.Abs(Axes.Y.Z) * Half.Y + Mathf.Abs(Axes.Z.Z) * Half.Z);
        return (Center - e, Center + e);
    }

    /// <summary>Separating axis test (Ericson, Real-Time Collision Detection 4.4.1).</summary>
    public static bool Overlap(in Obb a, in Obb b)
    {
        Span<Vector3> A = stackalloc Vector3[] { a.Axes.X, a.Axes.Y, a.Axes.Z };
        Span<Vector3> B = stackalloc Vector3[] { b.Axes.X, b.Axes.Y, b.Axes.Z };
        Span<float> ae = stackalloc float[] { a.Half.X, a.Half.Y, a.Half.Z };
        Span<float> be = stackalloc float[] { b.Half.X, b.Half.Y, b.Half.Z };
        Span<float> R = stackalloc float[9], AR = stackalloc float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) { R[i * 3 + j] = A[i].Dot(B[j]); AR[i * 3 + j] = Mathf.Abs(R[i * 3 + j]) + 1e-6f; }
        var d = b.Center - a.Center;
        Span<float> t = stackalloc float[] { d.Dot(A[0]), d.Dot(A[1]), d.Dot(A[2]) };
        float ra, rb;
        for (int i = 0; i < 3; i++)
        {
            ra = ae[i]; rb = be[0] * AR[i * 3] + be[1] * AR[i * 3 + 1] + be[2] * AR[i * 3 + 2];
            if (Mathf.Abs(t[i]) > ra + rb) return false;
        }
        for (int j = 0; j < 3; j++)
        {
            ra = ae[0] * AR[j] + ae[1] * AR[3 + j] + ae[2] * AR[6 + j]; rb = be[j];
            if (Mathf.Abs(t[0] * R[j] + t[1] * R[3 + j] + t[2] * R[6 + j]) > ra + rb) return false;
        }
        for (int i = 0; i < 3; i++)
        {
            int i1 = (i + 1) % 3, i2 = (i + 2) % 3;
            for (int j = 0; j < 3; j++)
            {
                int j1 = (j + 1) % 3, j2 = (j + 2) % 3;
                ra = ae[i1] * AR[i2 * 3 + j] + ae[i2] * AR[i1 * 3 + j];
                rb = be[j1] * AR[i * 3 + j2] + be[j2] * AR[i * 3 + j1];
                if (Mathf.Abs(t[i2] * R[i1 * 3 + j] - t[i1] * R[i2 * 3 + j]) > ra + rb) return false;
            }
        }
        return true;
    }
}

/// <summary>One piece as seen by the stability solver.</summary>
public sealed class StabilityNode
{
    public required PieceDef Def;
    public Vector3 Pos;
    public Basis Basis;
    public Vector3 Com;
    public readonly List<Obb> Colliders = new();
    public readonly List<Obb> Bounds = new();   // colliders grown by 0.3 m, used to find neighbours
    public bool Grounded;
    public float Support;
    public bool Falls;
    public readonly List<(StabilityNode Other, Obb Collider)> Touching = new();

    public bool Participates => Def.Material != null;

    public static StabilityNode Create(PieceDef def, Vector3 pos, Basis basis)
    {
        var n = new StabilityNode { Def = def, Pos = pos, Basis = basis, Com = pos + basis * def.ComOffset };
        foreach (var b in def.Boxes)
        {
            if (b.Fake) continue;
            var axes = basis * new Basis(b.Rotation);
            var col = new Obb(pos + basis * b.Center, axes, b.Size / 2, b.IsMesh);
            n.Colliders.Add(col);
            // WearNTear.SetupColliders: box -> its own rotated box, others -> world AABB; then size += 0.3, halved.
            if (b.IsMesh)
            {
                var (mn, mx) = col.Aabb();
                n.Bounds.Add(new Obb((mn + mx) / 2, Basis.Identity, (mx - mn + new Vector3(0.3f, 0.3f, 0.3f)) / 2));
            }
            else n.Bounds.Add(new Obb(col.Center, axes, (b.Size + new Vector3(0.3f, 0.3f, 0.3f)) / 2));
        }
        // Terrain is the plane y = 0: touching it gives full support, like the game's terrain layer check.
        foreach (var bd in n.Bounds)
            if (bd.Aabb().min.Y <= 0f) { n.Grounded = true; break; }
        return n;
    }
}

/// <summary>
/// Faithful port of WearNTear.UpdateSupport / GetMaterialProperties (Valheim). The game updates pieces
/// one at a time over many frames; here the same rule is iterated to its fixed point from the ground up,
/// then pieces under their material's minimum support collapse and the rest is recomputed.
/// </summary>
public static class Stability
{
    const float Cell = 4f;

    public static void Solve(IReadOnlyList<StabilityNode> nodes, Dictionary<string, MaterialProps> mats)
    {
        FindContacts(nodes);
        foreach (var n in nodes) n.Falls = false;

        for (int pass = 0; pass < 64; pass++)
        {
            foreach (var n in nodes)
                n.Support = n.Participates && n.Grounded ? mats[n.Def.Material!].MaxSupport : 0f;

            for (int iter = 0; iter < 2000; iter++)
            {
                bool changed = false;
                foreach (var n in nodes)
                {
                    if (!n.Participates || n.Grounded || n.Falls) continue;
                    float s = Compute(n, mats);
                    if (Mathf.Abs(s - n.Support) > 1e-3f) { n.Support = s; changed = true; }
                }
                if (!changed) break;
            }

            bool collapsed = false;
            foreach (var n in nodes)
            {
                if (!n.Participates || n.Falls) continue;
                if (n.Support < mats[n.Def.Material!].MinSupport) { n.Falls = true; n.Support = 0; collapsed = true; }
            }
            if (!collapsed) break;
        }
    }

    /// <summary>Support a piece would get from the current (already solved) neighbours — used for the ghost.</summary>
    public static float Evaluate(StabilityNode ghost, IReadOnlyList<StabilityNode> nodes, Dictionary<string, MaterialProps> mats)
    {
        if (!ghost.Participates) return 0;
        var m = mats[ghost.Def.Material!];
        if (ghost.Grounded) return m.MaxSupport;
        ghost.Touching.Clear();
        var (gmin, gmax) = BoundsAabb(ghost);
        foreach (var o in nodes)
        {
            if (!o.Participates || o.Falls || !o.Def.Supports) continue;
            if (o.Pos.DistanceTo(ghost.Pos) > o.Def.Radius + ghost.Def.Radius + 1f) continue;
            AddContacts(ghost, o, gmin, gmax);
        }
        return Compute(ghost, mats);
    }

    public static float MaxSupport(StabilityNode n, Dictionary<string, MaterialProps> mats) =>
        n.Participates ? mats[n.Def.Material!].MaxSupport : 0;

    /// <summary>WearNTear.GetSupportColorValue: -1 = full support (blue), else 0 (red) .. 1 (green).</summary>
    public static float ColorValue(float support, MaterialProps m)
    {
        if (support >= m.MaxSupport) return -1f;
        return Mathf.Clamp((support - m.MinSupport) / (m.MaxSupport * 0.5f - m.MinSupport), 0f, 1f);
    }

    /// <summary>WearNTear.Highlight colours.</summary>
    public static Color HighlightColor(float colorValue)
    {
        if (colorValue < 0) return new Color(0.6f, 0.8f, 1f);
        var c = new Color(1, 0, 0).Lerp(new Color(0, 1, 0), colorValue);
        return Color.FromHsv(c.H, Mathf.Lerp(1f, 0.5f, colorValue), Mathf.Min(1f, Mathf.Lerp(1.2f, 0.9f, colorValue)));
    }

    static float Compute(StabilityNode n, Dictionary<string, MaterialProps> mats)
    {
        var m = mats[n.Def.Material!];
        float best = 0f;
        Span<Vector3> pts = stackalloc Vector3[64];
        Span<float> vals = stackalloc float[64];
        int count = 0;
        var com = n.Com;
        foreach (var (o, col) in n.Touching)
        {
            if (o.Falls || !o.Def.Supports || !o.Participates) continue;
            float dist = com.DistanceTo(o.Com) + 0.1f;
            float distPos = com.DistanceTo(o.Pos) + 0.1f;
            if (distPos < dist && !n.Def.ForceCorrectCom) dist = distPos;
            float s2 = o.Support;
            best = Mathf.Max(best, s2 - m.HorizontalLoss * dist * s2);

            // FindSupportPoint: closest point of the other collider (non-convex meshes: halfway between COMs).
            var v = col.IsMesh ? (com + o.Com) * 0.5f : col.ClosestPoint(com);
            if (v.Y < com.Y + 0.05f)
            {
                var dir = (v - com).Normalized();
                if (dir.Y < 0f)
                {
                    float t = Mathf.Acos(1f - Mathf.Abs(dir.Y)) / (Mathf.Pi / 2f);
                    float loss = Mathf.Lerp(m.HorizontalLoss, m.VerticalLoss, t);
                    best = Mathf.Max(best, s2 - loss * dist * s2);
                }
                if (count < pts.Length) { pts[count] = v; vals[count] = s2 - m.VerticalLoss * dist * s2; count++; }
            }
        }
        // Two supports on opposite sides (>= 100° apart seen from above) share the load.
        for (int l = 0; l < count - 1; l++)
        {
            var from = pts[l] - com; from.Y = 0;
            for (int k = l + 1; k < count; k++)
            {
                float avg = (vals[l] + vals[k]) * 0.5f;
                if (avg <= best) continue;
                var to = pts[k] - com; to.Y = 0;
                if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) continue;
                if (Mathf.RadToDeg(from.AngleTo(to)) >= 100f) best = avg;
            }
        }
        return Mathf.Min(best, m.MaxSupport);
    }

    static (Vector3, Vector3) BoundsAabb(StabilityNode n)
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var max = -min;
        foreach (var b in n.Bounds) { var (a, c) = b.Aabb(); min = min.Min(a); max = max.Max(c); }
        return (min, max);
    }

    static void AddContacts(StabilityNode n, StabilityNode o, Vector3 nmin, Vector3 nmax)
    {
        foreach (var col in o.Colliders)
        {
            var (cmin, cmax) = col.Aabb();
            if (cmin.X > nmax.X || cmin.Y > nmax.Y || cmin.Z > nmax.Z || cmax.X < nmin.X || cmax.Y < nmin.Y || cmax.Z < nmin.Z) continue;
            foreach (var bd in n.Bounds)
                if (Obb.Overlap(bd, col)) { n.Touching.Add((o, col)); break; }
        }
    }

    static void FindContacts(IReadOnlyList<StabilityNode> nodes)
    {
        var grid = new Dictionary<(int, int, int), List<StabilityNode>>();
        var boxes = new Dictionary<StabilityNode, (Vector3 min, Vector3 max)>();
        static (int, int, int) Key(Vector3 p) => ((int)Mathf.Floor(p.X / Cell), (int)Mathf.Floor(p.Y / Cell), (int)Mathf.Floor(p.Z / Cell));
        foreach (var n in nodes)
        {
            n.Touching.Clear();
            if (n.Bounds.Count == 0) continue;
            var bb = BoundsAabb(n);
            boxes[n] = bb;
            var (a, b) = (Key(bb.Item1), Key(bb.Item2));
            for (int x = a.Item1; x <= b.Item1; x++)
                for (int y = a.Item2; y <= b.Item2; y++)
                    for (int z = a.Item3; z <= b.Item3; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) grid[(x, y, z)] = l = new();
                        l.Add(n);
                    }
        }
        var seen = new HashSet<StabilityNode>();
        foreach (var n in nodes)
        {
            if (!n.Participates || !boxes.TryGetValue(n, out var bb)) continue;
            seen.Clear();
            var (a, b) = (Key(bb.min), Key(bb.max));
            for (int x = a.Item1; x <= b.Item1; x++)
                for (int y = a.Item2; y <= b.Item2; y++)
                    for (int z = a.Item3; z <= b.Item3; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) continue;
                        foreach (var o in l)
                            if (o != n && seen.Add(o)) AddContacts(n, o, bb.min, bb.max);
                    }
        }
    }
}
