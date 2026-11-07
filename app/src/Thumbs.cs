using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace ValheimPlanner;

/// <summary>
/// Renders a small picture of each piece (from its shape, in its own little 3D world) for the build menu,
/// one per frame or two, and caches them in user://thumbs so it only happens once.
/// </summary>
public partial class Thumbs : Node
{
    const int Size = 128;
    public static string CacheDir => ProjectSettings.GlobalizePath("user://thumbs");

    readonly Dictionary<string, Texture2D> _done = new();
    readonly Queue<(PieceDef Def, Action<Texture2D> Then)> _queue = new();
    readonly HashSet<string> _queued = new();
    SubViewport _vp = null!;
    Camera3D _cam = null!;
    Node3D _stage = null!;
    bool _busy;
    public bool Busy => _busy || _queue.Count > 0;

    public override void _Ready()
    {
        _vp = new SubViewport
        {
            Size = new Vector2I(Size, Size), TransparentBg = true, OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_vp);
        _vp.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.75f, 0.72f, 0.68f), AmbientLightEnergy = 0.8f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
            },
        });
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, 35, 0), LightEnergy = 1.4f, LightColor = new Color(1f, 0.95f, 0.88f) });
        _cam = new Camera3D { Fov = 30, Current = true };
        _vp.AddChild(_cam);
        _stage = new Node3D();
        _vp.AddChild(_stage);
        System.IO.Directory.CreateDirectory(CacheDir);
    }

    /// <summary>Cached picture right away, or null and the callback later.</summary>
    public Texture2D? Get(PieceDef def, Action<Texture2D> then)
    {
        if (_done.TryGetValue(def.Prefab, out var t)) return t;
        var file = System.IO.Path.Combine(CacheDir, def.Prefab + ".png");
        if (System.IO.File.Exists(file))
        {
            var img = Image.LoadFromFile(file);
            if (img != null) return _done[def.Prefab] = ImageTexture.CreateFromImage(img);
        }
        if (_queued.Add(def.Prefab)) _queue.Enqueue((def, then));
        if (!_busy) _ = Pump();
        return null;
    }

    public static void ClearCache()
    {
        try { if (System.IO.Directory.Exists(CacheDir)) System.IO.Directory.Delete(CacheDir, true); }
        catch (Exception e) { GD.PushWarning($"thumbs: {e.Message}"); }
    }

    async Task Pump()
    {
        _busy = true;
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        while (_queue.Count > 0)
        {
            var (def, then) = _queue.Dequeue();
            foreach (var c in _stage.GetChildren()) c.QueueFree();
            var model = PieceVisuals.BuildSolid(def);
            _stage.AddChild(model);
            Frame(model);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = _vp.GetTexture().GetImage();
            img.SavePng(System.IO.Path.Combine(CacheDir, def.Prefab + ".png"));
            var tex = ImageTexture.CreateFromImage(img);
            _done[def.Prefab] = tex;
            _queued.Remove(def.Prefab);
            then(tex);
        }
        foreach (var c in _stage.GetChildren()) c.QueueFree();
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _busy = false;
    }

    /// <summary>Three-quarter view that fits the whole piece.</summary>
    void Frame(Node3D model)
    {
        Aabb? box = null;
        foreach (var n in model.GetChildren())
            if (n is MeshInstance3D mi)
            {
                var b = mi.Transform * mi.GetAabb();
                box = box?.Merge(b) ?? b;
            }
        var aabb = box ?? new Aabb(new Vector3(-1, 0, -1), new Vector3(2, 2, 2));
        var c = aabb.GetCenter();
        float r = Mathf.Max(aabb.Size.Length() * 0.5f, 0.3f);
        var dir = new Vector3(0.85f, 0.65f, 1.15f).Normalized();
        float dist = r / Mathf.Sin(Mathf.DegToRad(_cam.Fov * 0.5f)) * 1.02f;
        _cam.Near = Mathf.Max(0.05f, dist - r * 2);
        _cam.Far = dist + r * 2;
        _cam.LookAtFromPosition(c + dir * dist, c, Vector3.Up);
    }
}
