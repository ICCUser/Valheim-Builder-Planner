using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;

namespace ValheimPlanner;

public partial class Main : Node3D
{
    // Same values as Player.UpdatePlacementGhost / FindClosestSnapPoints in the game.
    const float SnapRadius = 0.5f;        // max distance between two snap points to lock them together
    const float SnapSearchRadius = 10f;   // pieces whose origin is within this distance are considered
    const float RotStepDeg = 22.5f;       // Valheim rotates pieces in 16 steps
    const float PlaceRange = 60f;

    sealed class Placed
    {
        public required PieceDef Def;
        public required StaticBody3D Body;
        public Vector3 Pos;
        public int Rot;
        public required StabilityNode Node;
        public List<MeshInstance3D> Meshes = new();
        public int Level;
    }

    // floor-by-floor plan view
    const float LevelHeight = 2f;         // one storey = one 2 m wall
    bool _planMode;
    int _level;
    Camera3D _planCam = null!;
    Button _planBtn = null!;
    Label _planLabel = null!;
    StandardMaterial3D _fadedMat = null!;
    bool _panning;

    static int LevelOf(StabilityNode n)
    {
        float minY = n.Colliders.Count > 0 ? n.Colliders.Min(c => c.Aabb().min.Y) : n.Pos.Y;
        return Math.Max(0, (int)Mathf.Floor((minY + 0.3f) / LevelHeight));
    }

    int MaxLevel => _placed.Count == 0 ? 0 : _placed.Max(p => p.Level);

    bool _showStability = false;
    readonly Dictionary<int, StandardMaterial3D> _stabMats = new();
    StandardMaterial3D _fallMat = null!;
    Button _stabBtn = null!;

    PieceCatalog _cat = null!;
    Camera3D _cam = null!;
    Node3D _world = null!;
    readonly List<Placed> _placed = new();
    readonly Dictionary<ulong, Placed> _byBody = new();
    readonly List<List<(bool Added, Placed P)>> _undo = new();

    PieceDef? _current;
    int _rot;
    Node3D? _ghost;
    List<MeshInstance3D> _ghostMeshes = new();
    bool _ghostValid, _ghostSnapped;
    Vector3 _ghostPos;
    Placed? _hovered;
    bool _gridSnap;
    int _manualSnap = -1;                 // -1 = automatic, else index of the ghost snap point to use

    // camera: Valheim-like free flight, mouse captured unless the menu cursor is toggled with Tab
    float _yaw = Mathf.DegToRad(-35), _pitch = Mathf.DegToRad(-30);
    bool _free;
    Control _crosshair = null!;

    // UI
    OptionButton _langSel = null!;
    LineEdit _search = null!;
    ItemList _list = null!;
    Label _info = null!, _status = null!, _help = null!;
    RichTextLabel _tally = null!;
    DirectionalLight3D _sun = null!;
    Godot.Environment _env = null!;
    readonly Dictionary<PieceDef, Color> _typeColors = new();
    readonly Dictionary<Color, StandardMaterial3D> _typeMats = new();
    static readonly Color[] Palette =
    {
        new(0.90f, 0.35f, 0.25f), new(0.25f, 0.55f, 0.90f), new(0.95f, 0.75f, 0.20f), new(0.35f, 0.75f, 0.35f),
        new(0.70f, 0.40f, 0.85f), new(0.20f, 0.80f, 0.80f), new(0.95f, 0.55f, 0.75f), new(0.60f, 0.45f, 0.25f),
        new(0.55f, 0.85f, 0.20f), new(0.95f, 0.55f, 0.15f), new(0.45f, 0.45f, 0.75f), new(0.80f, 0.80f, 0.80f),
    };
    /// <summary>Movement keys as printed on this keyboard (WASD on QWERTY, ZQSD on AZERTY…).</summary>
    static string MoveKeys => DisplayServer.GetName() == "headless" ? "WASD" : string.Concat(new[] { Key.W, Key.A, Key.S, Key.D }
        .Select(k => OS.GetKeycodeString(DisplayServer.KeyboardGetKeycodeFromPhysical(k))));
    static string Help3D => Loc.F("Tab: menu / mouse · Mouse: look · {0}: fly · Space/C: up/down · Shift: fast · Left click: place · Wheel or R: rotate · Ctrl+wheel: pick snap point · Alt: no snapping · Middle click: remove · Shift+middle click: pick piece · G: 0.5 m grid · Ctrl+Z: undo · Esc: put piece away · P: floor view · V: stability", MoveKeys);
    static string HelpPlan => Loc.F("Page Up / Page Down: change floor · Wheel: zoom · Right drag or {0}: move view · Left click: place · R / Shift+R: rotate · Middle click: remove · Shift+middle click: pick piece · Ctrl+Z: undo · P: back to 3D", MoveKeys);
    FileDialog _saveDlg = null!, _openDlg = null!, _exportDlg = null!;
    readonly List<PieceDef> _listed = new();

    bool _started;
    string[] _args = System.Array.Empty<string>();

    public override void _Ready()
    {
        GetTree().Root.Theme = UiTheme.Create();   // Norse look everywhere, dialogs and popups included
        MigrateOldUserData();
        _args = OS.GetCmdlineUserArgs();
        _testMode = _args.Any(a => a.StartsWith("--screenshot=") || a == "--selftest");
        var langArg = _args.FirstOrDefault(a => a.StartsWith("--lang="));
        Loc.Init(langArg?["--lang=".Length..] ?? (_testMode ? "fr" : null));

        // Game data is read from the player's own install on first launch (and after game updates).
        var info = GameData.GameExtractor.ReadInfo(Setup.DataDir);
        if (info != null && !_args.Contains("--setup") && (_testMode || !GameData.GameExtractor.IsOutdated(info))) StartApp();
        else AddChild(new Setup(_args.Contains("--setup-fresh") ? null : info, StartApp));
    }

    /// <summary>Before the rename the data lived in Godot's default folder "app_userdata/Valheim Planner": bring it over once.</summary>
    static void MigrateOldUserData()
    {
        try
        {
            string newDir = Setup.DataDir;
            string oldDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "Valheim Planner");
            if (!System.IO.Directory.Exists(oldDir) || System.IO.File.Exists(System.IO.Path.Combine(newDir, "settings.json"))
                || System.IO.File.Exists(System.IO.Path.Combine(newDir, "autosave.vplan.json"))) return;
            System.IO.Directory.CreateDirectory(newDir);
            foreach (var name in new[] { "autosave.vplan.json", "settings.json", "pieces.json", "meshes.bin", "extract_info.json" })
            {
                var src = System.IO.Path.Combine(oldDir, name);
                if (System.IO.File.Exists(src)) System.IO.File.Copy(src, System.IO.Path.Combine(newDir, name), overwrite: false);
            }
            GD.Print($"Migrated user data from {oldDir}");
        }
        catch (System.Exception e) { GD.PushWarning($"Migration skipped: {e.Message}"); }
    }

    void StartApp()
    {
        _cat = PieceCatalog.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(Setup.DataDir, "pieces.json")));
        MeshLibrary.Load();
        GD.Print($"Loaded {_cat.Pieces.Count} pieces");
        _started = true;

        _fallMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.9f, 0.05f, 0.05f), EmissionEnabled = true, Emission = new Color(1f, 0.1f, 0.05f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _selMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.85f, 0.2f), EmissionEnabled = true, Emission = new Color(1f, 0.75f, 0.1f), EmissionEnergyMultiplier = 0.5f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var args = _args;
        Loc.Changed += OnLanguageChanged;
        BuildScene();
        BuildUi();
        // Drop a plan or a community blueprint on the window to open it.
        GetWindow().FilesDropped += files => { foreach (var file in files) OpenAnyFile(file); };
        RefreshList();
        UpdateTally();

        SetFreeCursor(false);
        if (!_testMode && FileAccess.FileExists(AutosavePath))
        {
            LoadPlan(AutosavePath);
            if (_placed.Count > 0) { _flash = Loc.N("Last plan restored ({0} piece)", "Last plan restored ({0} pieces)", _placed.Count); _flashUntil = Time.GetTicksMsec() + 4000; }
        }
        // Tool hook: --export-autosave-blueprint=<name> sends the last autosaved plan to every PlanBuild install found, then quits.
        var exportArg = args.FirstOrDefault(a => a.StartsWith("--export-autosave-blueprint="));
        if (exportArg != null)
        {
            if (_placed.Count == 0 && FileAccess.FileExists(AutosavePath)) { _testMode = true; LoadPlan(AutosavePath); }
            string bpName = exportArg["--export-autosave-blueprint=".Length..];
            var text = PlanBuildExport.Build(bpName, _placed.Select(p => (p.Def, p.Pos, p.Rot)), RotStepDeg);
            foreach (var (label, dir, has) in PlanBuildExport.Targets(GameData.GameExtractor.ReadInfo(Setup.DataDir)?.GamePath).Where(t => t.HasPlanBuild))
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, PlanBuildExport.SafeFileName(bpName) + ".blueprint"), text);
                GD.Print($"Blueprint ({_placed.Count} pieces) -> {label}: {dir}");
            }
            GetTree().Quit();
            return;
        }
        var shot = args.FirstOrDefault(a => a.StartsWith("--screenshot="));
        if (shot != null) _ = RunScreenshot(shot["--screenshot=".Length..]);
        if (args.Contains("--selftest")) { SelfTest(); GetTree().Quit(); }
    }

    // ------------------------------------------------------------------ scene
    void BuildScene()
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.32f, 0.50f, 0.78f),
            SkyHorizonColor = new Color(0.78f, 0.80f, 0.80f),
            GroundHorizonColor = new Color(0.62f, 0.66f, 0.62f),
            GroundBottomColor = new Color(0.22f, 0.26f, 0.20f),
            SunAngleMax = 20f,
            SkyCurve = 0.12f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.65f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapExposure = 0.95f,
            SsaoEnabled = true, SsaoRadius = 1.2f, SsaoIntensity = 2.2f,
            SsilEnabled = true, SsilIntensity = 0.6f,
            GlowEnabled = true, GlowIntensity = 0.25f, GlowBloom = 0.03f,
            FogEnabled = true, FogLightColor = new Color(0.72f, 0.78f, 0.82f), FogDensity = 0.0025f, FogAerialPerspective = 0.4f, FogSkyAffect = 0.2f,
            AdjustmentEnabled = true, AdjustmentSaturation = 1.08f, AdjustmentContrast = 1.04f,
        };
        _env = env;
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(_sun = new DirectionalLight3D
        {
            ShadowEnabled = true,
            RotationDegrees = new Vector3(-50, -35, 0),
            LightEnergy = 1.35f,
            LightColor = new Color(1f, 0.95f, 0.86f),
            LightAngularDistance = 1.2f,
            ShadowBlur = 1.2f,
            DirectionalShadowMaxDistance = 120f,
        });

        // Meadow-like ground with a 2 m / 0.5 m grid (2 m = one Valheim floor tile) fading with distance.
        var shader = new Shader
        {
            Code = """
            shader_type spatial;
            varying vec3 wp;
            void vertex() { wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
            float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
            float noise(vec2 p) {
                vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
                return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
            }
            float fbm(vec2 p) { float v = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { v += a * noise(p); p *= 2.07; a *= 0.5; } return v; }
            float grid(vec2 p, float cell) {
                vec2 q = p / cell;
                vec2 g = abs(fract(q - 0.5) - 0.5) / fwidth(q);
                return 1.0 - min(min(g.x, g.y), 1.0);
            }
            void fragment() {
                float big = fbm(wp.xz * 0.04), mid = fbm(wp.xz * 0.35), fine = noise(wp.xz * 6.0);
                vec3 grass = mix(vec3(0.13, 0.21, 0.07), vec3(0.24, 0.30, 0.10), big);
                grass = mix(grass, vec3(0.30, 0.27, 0.14), smoothstep(0.62, 0.8, mid) * 0.5);
                grass *= 0.88 + 0.24 * fine;
                float dist = length(CAMERA_POSITION_WORLD - wp);
                float fade = 1.0 - smoothstep(25.0, 70.0, dist);
                float major = grid(wp.xz, 2.0) * fade;
                float minor = grid(wp.xz, 0.5) * (1.0 - smoothstep(8.0, 25.0, dist));
                float axis = clamp((1.0 - min(abs(wp.x) / fwidth(wp.x), 1.0)) + (1.0 - min(abs(wp.z) / fwidth(wp.z), 1.0)), 0.0, 1.0) * fade;
                vec3 c = mix(grass, vec3(0.92, 0.95, 0.85), major * 0.16);
                c = mix(c, vec3(0.92, 0.95, 0.85), minor * 0.06);
                c = mix(c, vec3(0.85, 0.35, 0.22), axis * 0.7);
                ALBEDO = c;
                ROUGHNESS = 0.95;
            }
            """
        };
        var ground = new StaticBody3D { Name = "Ground" };
        ground.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1200, 1200) }, MaterialOverride = new ShaderMaterial { Shader = shader } });
        ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        AddChild(ground);

        _world = new Node3D { Name = "Build" };
        AddChild(_world);

        _cam = new Camera3D { Position = new Vector3(-8, 7, 10), Fov = 70, Current = true };
        AddChild(_cam);
        ApplyCamRotation();

        _planCam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 30, Far = 1000, RotationDegrees = new Vector3(-90, 0, 0) };
        AddChild(_planCam);
        _fadedMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.85f, 0.85f, 0.9f, 0.22f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    void ApplyCamRotation() => _cam.Rotation = new Vector3(_pitch, _yaw, 0);

    // ------------------------------------------------------------------ UI
    // Layout: top bar (file, views, language) · left: pieces (categories, search, picture grid)
    //         right: selected piece + materials · bottom: status and controls.
    const int TopH = 50, LeftW = 340, RightW = 300, Gap = 8;
    int _catId = 2;                                   // "Building" tab first, like the hammer
    HFlowContainer _cats = null!;
    TextureRect _infoIcon = null!;
    Thumbs _thumbs = null!;

    void BuildUi()
    {
        // A CanvasLayer does not pass themes down, so everything hangs off one themed root control.
        var canvas = new CanvasLayer { Visible = !OS.GetCmdlineUserArgs().Contains("--clean") };   // --clean: 3D only, for banners
        AddChild(canvas);
        var layer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = GetTree().Root.Theme };
        layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        canvas.AddChild(layer);
        _thumbs = new Thumbs();
        AddChild(_thumbs);

        // ---- top bar
        var topBar = new PanelContainer();
        topBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        topBar.OffsetLeft = Gap; topBar.OffsetTop = Gap; topBar.OffsetRight = -Gap; topBar.OffsetBottom = Gap + TopH;
        layer.AddChild(topBar);
        var tb = new HBoxContainer();
        tb.AddThemeConstantOverride("separation", 8);
        topBar.AddChild(tb);
        tb.AddChild(new TextureRect { Texture = GD.Load<Texture2D>("res://icon.png"), CustomMinimumSize = new Vector2(36, 36), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
        var title = new Label { Text = AppInfo.Name.ToUpperInvariant(), TooltipText = "v" + AppInfo.Version, AutoTranslateMode = AutoTranslateModeEnum.Disabled, VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeFontOverride("font", UiTheme.SerifBold);
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", UiTheme.Gold);
        tb.AddChild(title);
        tb.AddChild(new VSeparator());

        var bNew = UiTheme.Btn("New");
        var bOpen = UiTheme.Btn("Open…");
        var bSave = UiTheme.Btn("Save…");
        var bList = BuildExportMenu(layer);
        var confirmNew = new ConfirmationDialog { DialogText = "Clear the whole current plan?\n(Save it first if you want to keep it.)", Title = "New plan", OkButtonText = "Clear", CancelButtonText = "Cancel" };
        confirmNew.Confirmed += () => { ClearAll(); if (!_planMode) SetFreeCursor(false); };
        layer.AddChild(confirmNew);
        bNew.Pressed += () => { if (_placed.Count == 0) return; SetFreeCursor(true); confirmNew.PopupCentered(); };
        bOpen.Pressed += () => { SetFreeCursor(true); _openDlg.PopupCentered(new Vector2I(900, 600)); };
        bSave.Pressed += () => { SetFreeCursor(true); _saveDlg.PopupCentered(new Vector2I(900, 600)); };

        foreach (var b in new[] { bNew, bOpen, bSave, bList }) tb.AddChild(b);

        tb.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _stabBtn = UiTheme.Btn("Stability view (V)", toggle: true);
        _stabBtn.ButtonPressed = _showStability;
        _stabBtn.Toggled += on => { _showStability = on; ApplyColors(); };
        _planBtn = UiTheme.Btn("Floor view (P)", toggle: true);
        _planBtn.Toggled += SetPlanMode;
        _guideBtn = UiTheme.Btn("Guide (B)", toggle: true);
        _guideBtn.Toggled += SetGuide;
        tb.AddChild(_guideBtn); tb.AddChild(_stabBtn); tb.AddChild(_planBtn);
        tb.AddChild(new VSeparator());
        // Language picker: native names, so anyone can find their own language.
        _langSel = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Language", AutoTranslateMode = AutoTranslateModeEnum.Disabled, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(150, 0) };
        foreach (var l in Loc.Languages) _langSel.AddItem(l.Native);
        _langSel.Select(Array.IndexOf(Loc.Languages, Loc.Current));
        _langSel.ItemSelected += i => Loc.Apply(Loc.Languages[(int)i]);
        tb.AddChild(_langSel);

        // ---- left: pieces
        var left = new PanelContainer();
        left.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide);
        left.OffsetLeft = Gap; left.OffsetTop = Gap * 2 + TopH; left.OffsetRight = Gap + LeftW; left.OffsetBottom = -Gap;
        layer.AddChild(left);
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 8);
        left.AddChild(vb);
        vb.AddChild(UiTheme.Header("Pieces"));
        _cats = new HFlowContainer();
        _cats.AddThemeConstantOverride("h_separation", 4);
        _cats.AddThemeConstantOverride("v_separation", 4);
        vb.AddChild(_cats);
        FillCategories(_catId);
        _search = new LineEdit { PlaceholderText = "Search for a piece…", ClearButtonEnabled = true };
        _search.TextChanged += _ => RefreshList();
        vb.AddChild(_search);
        // Picture grid like the game's build menu; names in the tooltip and under each picture.
        _list = new ItemList
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None,
            IconMode = ItemList.IconModeEnum.Top, MaxColumns = 0, SameColumnWidth = true, FixedColumnWidth = 92,
            FixedIconSize = new Vector2I(78, 78), MaxTextLines = 2, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _list.AddThemeFontSizeOverride("font_size", 12);
        _list.ItemSelected += i => { SelectPiece(_listed[(int)i]); if (!_planMode) SetFreeCursor(false); };
        vb.AddChild(_list);

        // ---- right: selected piece + materials
        var right = new PanelContainer();
        right.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);
        right.OffsetLeft = -Gap - RightW; right.OffsetTop = Gap * 2 + TopH; right.OffsetRight = -Gap; right.OffsetBottom = -Gap;
        layer.AddChild(right);
        var rv = new VBoxContainer();
        rv.AddThemeConstantOverride("separation", 8);
        right.AddChild(rv);
        rv.AddChild(UiTheme.Header("Selected piece"));
        var infoRow = new HBoxContainer();
        infoRow.AddThemeConstantOverride("separation", 8);
        rv.AddChild(infoRow);
        _infoIcon = new TextureRect { CustomMinimumSize = new Vector2(84, 84), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        infoRow.AddChild(_infoIcon);
        _info = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 84) };
        _info.AddThemeFontSizeOverride("font_size", 13);
        infoRow.AddChild(_info);
        _info.Text = Loc.T("Choose a piece on the left, or aim at one and press Shift+middle click.");
        rv.AddChild(BuildGuidePanel());
        rv.AddChild(UiTheme.Header("Plan"));
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        rv.AddChild(scroll);
        _tally = new RichTextLabel { AutoTranslateMode = AutoTranslateModeEnum.Disabled, BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        _tally.AddThemeFontSizeOverride("normal_font_size", 14);
        _tally.AddThemeFontOverride("bold_font", UiTheme.SerifBold);
        scroll.AddChild(_tally);

        // ---- bottom: status + controls
        var bottom = new PanelContainer();
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottom.OffsetLeft = Gap * 2 + LeftW; bottom.OffsetRight = -Gap * 2 - RightW; bottom.OffsetTop = -Gap - 78; bottom.OffsetBottom = -Gap;
        layer.AddChild(bottom);
        var bv = new VBoxContainer();
        bv.AddThemeConstantOverride("separation", 2);
        bottom.AddChild(bv);
        _status = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, ClipText = true };
        _status.AddThemeColorOverride("font_color", UiTheme.Gold.Lightened(0.15f));
        bv.AddChild(_status);
        _help = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = Help3D, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _help.AddThemeColorOverride("font_color", UiTheme.TextDim);
        _help.AddThemeFontSizeOverride("font_size", 12);
        bv.AddChild(_help);

        // ---- floor banner (floor view)
        var banner = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        banner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        banner.OffsetTop = Gap * 3 + TopH; banner.GrowHorizontal = Control.GrowDirection.Both;
        _planLabel = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, HorizontalAlignment = HorizontalAlignment.Center };
        _planLabel.AddThemeFontSizeOverride("font_size", 18);
        _planLabel.AddThemeColorOverride("font_color", UiTheme.Gold);
        banner.AddChild(_planLabel);
        layer.AddChild(banner);

        // ---- crosshair
        var cc = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        cc.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var cross = new Label { Text = "+", MouseFilter = Control.MouseFilterEnum.Ignore, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        cross.AddThemeFontSizeOverride("font_size", 26);
        cross.AddThemeColorOverride("font_color", UiTheme.Gold.Lightened(0.3f));
        cross.AddThemeColorOverride("font_outline_color", Colors.Black);
        cross.AddThemeConstantOverride("outline_size", 4);
        cc.AddChild(cross);
        layer.AddChild(cc);
        _crosshair = cc;

        // ---- file dialogs
        _saveDlg = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, Title = "Save plan", UseNativeDialog = true };
        _saveDlg.AddFilter("*.vplan.json", AppInfo.Name);
        _saveDlg.FileSelected += path => { SavePlan(path); };
        _openDlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Title = "Open plan", UseNativeDialog = true };
        _openDlg.AddFilter("*.vplan.json, *.blueprint, *.vbuild", AppInfo.Name + ", PlanBuild, BuildShare");
        _openDlg.FileSelected += OpenAnyFile;
        layer.AddChild(_saveDlg); layer.AddChild(_openDlg);
        _exportDlg = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, Title = "Export build list", UseNativeDialog = true, CurrentFile = "build_list.txt" };
        _exportDlg.AddFilter("*.txt", "Text");
        _exportDlg.FileSelected += ExportList;
        layer.AddChild(_exportDlg);

        _rectView = new ColorRect { Color = new Color(1f, 0.85f, 0.2f, 0.18f), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_rectView);
    }

    /// <summary>Category tabs (game names, already translated) as toggle buttons.</summary>
    void FillCategories(int selectId)
    {
        _catId = selectId;
        foreach (var c in _cats.GetChildren()) c.QueueFree();
        var group = new ButtonGroup();
        void Add(int id, string label)
        {
            var b = new Button
            {
                Text = label, ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None,
                AutoTranslateMode = AutoTranslateModeEnum.Disabled, ButtonPressed = id == _catId,
            };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Pressed += () => { _catId = id; RefreshList(); };
            _cats.AddChild(b);
        }
        Add(-1, Loc.T("All"));
        foreach (var (id, label) in _cat.Categories) Add(id, label);
    }

    /// <summary>Static button/label texts re-translate themselves; texts built in code are rebuilt here.</summary>
    void OnLanguageChanged()
    {
        FillCategories(_catId);
        FillExportMenu();
        RefreshGuide();
        RefreshList();
        _help.Text = _planMode ? HelpPlan : Help3D;
        if (_current != null) { int rot = _rot; SelectPiece(_current); _rot = rot; }
        UpdatePlanLabel();
        UpdateTally();
    }

    void RefreshList()
    {
        string q = _search.Text.Trim();
        _listed.Clear();
        _list.Clear();
        foreach (var p in _cat.Pieces)
        {
            if (_catId >= 0 && p.Category != _catId) continue;
            if (q.Length > 0 && !p.Label.Contains(q, StringComparison.OrdinalIgnoreCase) && !p.Prefab.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _listed.Add(p);
            var def = p;
            var icon = _thumbs.Get(p, tex =>
            {
                int idx = _listed.IndexOf(def);
                if (idx >= 0 && idx < _list.ItemCount) _list.SetItemIcon(idx, tex);
                if (_current == def) _infoIcon.Texture = tex;
            });
            int i = _list.AddItem(p.Label, icon);
            _list.SetItemTooltip(i, $"{p.Label}  {p.SizeText}\n{CostText(p)}");
        }
        if (_current != null && _listed.IndexOf(_current) is var sel and >= 0) _list.Select(sel);
    }

    static string CostText(PieceDef p) => string.Join(", ", p.Costs.Select(c => $"{c.Amount} {c.Label}"));

    void SelectPiece(PieceDef? def)
    {
        _current = def;
        _ghost?.QueueFree();
        _ghost = null;
        if (def == null) { _info.Text = Loc.T("Choose a piece on the left, or aim at one and press Shift+middle click."); _infoIcon.Texture = null; _list.DeselectAll(); return; }
        _infoIcon.Texture = _thumbs.Get(def, tex => { if (_current == def) _infoIcon.Texture = tex; });
        _ghost = PieceVisuals.BuildGhost(def, out _ghostMeshes);
        AddChild(_ghost);
        _ghost.Visible = false;
        _manualSnap = -1;
        _info.Text = $"{def.Label}  {def.SizeText}\n" + Loc.F("Cost: {0}", CostText(def)) + "\n" + Loc.F("Station: {0}", def.Station ?? Loc.T("none")) + "\n" + Loc.F("Material: {0}", MaterialName(def.Material)) + "\n" + Loc.F("Snap points: {0}", def.Snaps.Count);
        int idx = _listed.IndexOf(def);
        if (idx >= 0) _list.Select(idx);
    }

    static string MaterialName(string? m) => m switch
    {
        "Wood" => Loc.T("Wood"), "HardWood" => Loc.T("Core wood"), "Timberwood" => Loc.T("Timber wood"), "Stone" => Loc.T("Stone"),
        "Marble" => Loc.T("Black marble"), "Ashstone" => Loc.T("Ashstone"), "Iron" => Loc.T("Iron"), "Ancient" => Loc.T("Ancient"), "Ice" => Loc.T("Ice"),
        null => "—", _ => m,
    };

    void UpdateTally()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("Pieces placed: {0}", _placed.Count));
        sb.AppendLine();
        sb.AppendLine(Loc.T("Materials needed:"));
        var totals = _placed.SelectMany(p => p.Def.Costs).GroupBy(c => c.Label).Select(g => (g.Key, g.Sum(c => c.Amount))).OrderByDescending(t => t.Item2);
        bool any = false;
        foreach (var (label, n) in totals) { sb.AppendLine($"  {n,5} × {label}"); any = true; }
        if (!any) sb.AppendLine("  " + Loc.T("(nothing yet)"));
        int falling = _placed.Count(p => p.Node.Falls);
        if (falling > 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ " + Loc.N("{0} piece would collapse!", "{0} pieces would collapse!", falling));
        }
        var stations = _placed.Select(p => p.Def.Station).Where(s => s != null).Distinct().ToList();
        if (stations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("Stations required:"));
            foreach (var s in stations) sb.AppendLine($"  • {s}");
        }
        if (_planMode) { sb.Insert(0, LevelReport()); UpdatePlanLabel(); }
        _tally.Text = sb.ToString().Replace("\r\n", "\n");
    }

    string LevelReport()
    {
        var sb = new StringBuilder();
        var here = _placed.Where(p => p.Level == _level).ToList();
        sb.AppendLine("[b]" + Loc.F("FLOOR {0} / {1}", _level + 1, MaxLevel + 1) + $"[/b]   ({_level * LevelHeight:0}–{(_level + 1) * LevelHeight:0} m)");
        sb.AppendLine(Loc.N("{0} piece to place, in order:", "{0} pieces to place, in order:", here.Count));
        foreach (var (step, lines) in BuildSteps.ForFloor(here.Select(p => p.Def)))
        {
            sb.AppendLine($" [color=#{UiTheme.Gold.ToHtml(false)}]{Loc.T(BuildSteps.Names[step])}[/color]");
            foreach (var ln in lines)
            {
                var c = _typeColors.TryGetValue(ln.Def, out var tc) ? tc : Colors.White;
                sb.AppendLine($"  [color=#{c.ToHtml(false)}]■[/color] {ln.Count,3} × {ln.Def.Label}");
            }
        }
        sb.AppendLine(Loc.T("Materials for this floor:"));
        foreach (var g in here.SelectMany(p => p.Def.Costs).GroupBy(c => c.Label).OrderByDescending(g => g.Sum(c => c.Amount)))
            sb.AppendLine($"  {g.Sum(c => c.Amount),5} × {g.Key}");
        sb.AppendLine();
        sb.AppendLine("── " + Loc.T("Whole plan") + " ──");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ plan view
    void SetPlanMode(bool on)
    {
        _planMode = on;
        if (_planBtn.ButtonPressed != on) _planBtn.SetPressedNoSignal(on);
        _planLabel.GetParent<Control>().Visible = on;
        _help.Text = on ? HelpPlan : Help3D;
        _sun.ShadowEnabled = !on;
        _env.FogEnabled = !on;   // fog seen from 300 m up would wash out the top-down view
        if (on)
        {
            _level = Math.Clamp(_level, 0, MaxLevel);
            FitPlanCamera();
            _planCam.MakeCurrent();
            SetFreeCursor(true);
        }
        else
        {
            _cam.MakeCurrent();
            SetFreeCursor(false);
        }
        ApplyColors();
        UpdatePlanLabel();
    }

    void ChangeLevel(int delta)
    {
        if (!_planMode) return;
        _level = Math.Clamp(_level + delta, 0, MaxLevel);
        ApplyColors();
        UpdatePlanLabel();
    }

    void UpdatePlanLabel() =>
        _planLabel.Text = "  " + Loc.F("Floor {0} / {1}   —   from {2} to {3} m", _level + 1, MaxLevel + 1, _level * LevelHeight, (_level + 1) * LevelHeight) + "  ";

    void FitPlanCamera()
    {
        Vector2 min = new(-6, -6), max = new(6, 6);
        if (_placed.Count > 0)
        {
            min = new Vector2(_placed.Min(p => p.Pos.X), _placed.Min(p => p.Pos.Z)) - new Vector2(4, 4);
            max = new Vector2(_placed.Max(p => p.Pos.X), _placed.Max(p => p.Pos.Z)) + new Vector2(4, 4);
        }
        var c = (min + max) / 2;
        _planCam.Position = new Vector3(c.X, 300, c.Y);
        var vp = GetViewport().GetVisibleRect().Size;
        float aspect = vp.X / Mathf.Max(1, vp.Y);
        _planCam.Size = Mathf.Max(max.Y - min.Y, (max.X - min.X) / aspect) * 1.15f;
    }

    // ------------------------------------------------------------------ input
    void SetFreeCursor(bool free)
    {
        _free = free;
        Input.MouseMode = free ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        if (_crosshair != null) _crosshair.Visible = !free;
        if (!free) GetViewport().GuiReleaseFocus();
    }

    public override void _Input(InputEvent e)
    {
        if (!_started) return;
        // Tab works everywhere, even while typing in the search box.
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab } && !_planMode)
        {
            SetFreeCursor(!_free);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_started) return;
        if (_planMode && PlanInput(e)) return;
        if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (!_free) GetViewport().GuiReleaseFocus();
            switch (mb.ButtonIndex)
            {
                case MouseButton.Left:
                    GetViewport().GuiReleaseFocus();
                    if (mb.CtrlPressed) { if (_hovered != null) ToggleSelect(_hovered); }
                    else if (GuideClick()) { }
                    else if (_pasting) PlacePaste();
                    else if (_current != null && _ghostValid) PlaceCurrent();
                    break;
                case MouseButton.Middle:
                    if (mb.ShiftPressed) Pipette(); else RemoveHovered();
                    break;
                case MouseButton.WheelUp or MouseButton.WheelDown:
                    int dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                    if (mb.CtrlPressed && _current != null && _current.Snaps.Count > 0)
                    {
                        // cycle: auto, 0, 1, ... n-1, auto
                        int n = _current.Snaps.Count;
                        _manualSnap = ((_manualSnap + 1 + dir) % (n + 1) + (n + 1)) % (n + 1) - 1;
                    }
                    else _rot = (_rot + (dir > 0 ? 1 : 15)) % 16;
                    break;
            }
        }
        else if (e is InputEventMouseMotion mm && !_free)
        {
            _yaw -= mm.Relative.X * 0.0025f;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.0025f, -1.55f, 1.55f);
            ApplyCamRotation();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.CtrlPressed)
            {
                if (k.Keycode == Key.Z) Undo();
                else if (k.Keycode == Key.C) CopySelection();
                else if (k.Keycode == Key.V) StartPaste();
                else if (k.Keycode == Key.A) SelectAllVisible();
                return;
            }
            if (k.Keycode == Key.Escape)
            {
                if (_pasting) CancelPaste();
                else if (_selection.Count > 0) ClearSelection();
                else SelectPiece(null);
            }
            else if (k.Keycode == Key.Delete) RemoveHovered();
            else if (k.PhysicalKeycode == Key.F) Pipette();
            else if (k.PhysicalKeycode == Key.G && !k.CtrlPressed) _gridSnap = !_gridSnap;
            else if (k.PhysicalKeycode == Key.V) _stabBtn.ButtonPressed = !_stabBtn.ButtonPressed;
            else if (k.PhysicalKeycode == Key.P) SetPlanMode(!_planMode);
            else if (k.PhysicalKeycode == Key.B) _guideBtn.ButtonPressed = !_guideBtn.ButtonPressed;
            else if (_guide && k.Keycode == Key.Right) GuideGo(_gStep + 1);
            else if (_guide && k.Keycode == Key.Left) GuideGo(_gStep - 1);
            else if (k.PhysicalKeycode == Key.R) _rot = (_rot + (k.ShiftPressed ? 15 : 1)) % 16;
        }
    }

    /// <summary>Plan view: wheel zooms, right-drag pans, Page Up/Down change floor. Returns true if consumed.</summary>
    bool PlanInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true, CtrlPressed: false } w:
                float f = w.ButtonIndex == MouseButton.WheelUp ? 0.88f : 1f / 0.88f;
                // zoom towards the cursor
                var vp = GetViewport().GetVisibleRect().Size;
                var before = _planCam.ProjectPosition(w.Position, 1);
                _planCam.Size = Mathf.Clamp(_planCam.Size * f, 4, 600);
                var after = _planCam.ProjectPosition(w.Position, 1);
                _planCam.Position += new Vector3(before.X - after.X, 0, before.Z - after.Z);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } r:
                _panning = r.Pressed;
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, ShiftPressed: true } ls:
                _rectSel = true; _rectStart = ls.Position;
                _rectView.Position = ls.Position; _rectView.Size = Vector2.Zero; _rectView.Visible = true;
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } lr when _rectSel:
                FinishRectSelect(lr.Position, lr.CtrlPressed);
                return true;
            case InputEventMouseMotion rm when _rectSel:
                var rr = new Rect2(_rectStart, rm.Position - _rectStart).Abs();
                _rectView.Position = rr.Position; _rectView.Size = rr.Size;
                return true;
            case InputEventMouseMotion m when _panning:
                float scale = _planCam.Size / GetViewport().GetVisibleRect().Size.Y;
                _planCam.Position -= new Vector3(m.Relative.X * scale, 0, m.Relative.Y * scale);
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.Pageup }:
                ChangeLevel(1); return true;
            case InputEventKey { Pressed: true, Keycode: Key.Pagedown }:
                ChangeLevel(-1); return true;
        }
        return false;
    }

    void Pipette()
    {
        if (_hovered == null) return;
        SelectPiece(_hovered.Def);
        _rot = _hovered.Rot;
    }

    public override void _Process(double delta)
    {
        if (!_started) return;
        float pulse = 0.25f + 0.35f * (0.5f + 0.5f * Mathf.Sin((float)Time.GetTicksMsec() / 150f));
        if (!_planMode)
            foreach (var p in _placed)
                if (p.Node.Falls && !_selection.Contains(p))
                    foreach (var mi in p.Meshes) mi.SetInstanceShaderParameter("emit_amt", pulse);
        MoveCamera((float)delta);
        UpdateHoverAndGhost();
    }

    void MoveCamera(float dt)
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit || Input.IsKeyPressed(Key.Ctrl)) return;
        if (_planMode)
        {
            var d = Vector3.Zero;
            if (Input.IsPhysicalKeyPressed(Key.W)) d.Z -= 1;
            if (Input.IsPhysicalKeyPressed(Key.S)) d.Z += 1;
            if (Input.IsPhysicalKeyPressed(Key.D)) d.X += 1;
            if (Input.IsPhysicalKeyPressed(Key.A)) d.X -= 1;
            _planCam.Position += d * _planCam.Size * 0.6f * dt * (Input.IsKeyPressed(Key.Shift) ? 3 : 1);
            return;
        }
        var dir = Vector3.Zero;
        var b = _cam.GlobalTransform.Basis;
        // Fly where you look, like the game's debug fly mode.
        var fwd = -b.Z;
        var right = b.X; right.Y = 0; right = right.Normalized();
        // Physical keys: W/A/S/D positions = Z/Q/S/D on AZERTY.
        if (Input.IsPhysicalKeyPressed(Key.W)) dir += fwd;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir -= fwd;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir += right;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir -= right;
        if (Input.IsPhysicalKeyPressed(Key.Space)) dir += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.C)) dir -= Vector3.Up;
        if (dir == Vector3.Zero) return;
        float speed = Input.IsKeyPressed(Key.Shift) ? 20f : 6f;
        _cam.Position += dir.Normalized() * speed * dt;
    }

    (Vector3 pos, Vector3 normal, Placed? hit)? Raycast()
    {
        var mp = _free ? GetViewport().GetMousePosition() : GetViewport().GetVisibleRect().Size / 2;
        var cam = _planMode ? _planCam : _cam;
        var from = cam.ProjectRayOrigin(mp);
        var to = from + cam.ProjectRayNormal(mp) * (_planMode ? 1000 : PlaceRange);
        var r = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (r.Count == 0) return null;
        var col = r["collider"].AsGodotObject() as Node;
        Placed? p = col != null && _byBody.TryGetValue(col.GetInstanceId(), out var pp) ? pp : null;
        return ((Vector3)r["position"], (Vector3)r["normal"], p);
    }

    void UpdateHoverAndGhost()
    {
        bool overUi = _free && GetViewport().GuiGetHoveredControl() != null;
        var hit = overUi ? null : Raycast();
        _hovered = hit?.hit;
        var status = new StringBuilder();
        if (_hovered != null)
        {
            status.Append(Loc.F("Aiming at: {0}", _hovered.Def.Label) + "   ");
            var n = _hovered.Node;
            if (n.Participates)
            {
                var m = _cat.Materials[n.Def.Material!];
                status.Append((n.Falls ? Loc.T("Stability: COLLAPSES") : Loc.F("Stability: {0:0} / {1:0} (min {2:0})", n.Support, m.MaxSupport, m.MinSupport) + (n.Grounded ? " — " + Loc.T("on the ground") : "")) + "   ");
            }
        }
        if (_current != null) status.Append((_manualSnap < 0 ? Loc.T("Snap: auto") : Loc.F("Snap: point {0}/{1}", _manualSnap + 1, _current.Snaps.Count)) + "   ");
        if (_gridSnap) status.Append("[" + Loc.T("0.5 m grid") + "]   ");
        if (_free && !_planMode) status.Append("[" + Loc.T("menu — Tab to go back") + "]");
        if (_selection.Count > 0) status.Append("   " + Loc.F("Selection: {0} (Ctrl+C copy · Del delete · Esc clear)", _selection.Count));
        if (Time.GetTicksMsec() < _flashUntil) status.Insert(0, "► " + _flash + "   ");
        _status.Text = status.ToString();

        if (_pasting) { UpdatePaste(hit); return; }

        if (_ghost == null || _current == null) return;
        if (hit == null) { _ghost.Visible = false; _ghostValid = false; return; }

        var basis = new Basis(Vector3.Up, Mathf.DegToRad(_rot * RotStepDeg));
        var (point, normal, _) = hit.Value;
        var pos = RestOnSurface(_current, basis, point, normal);
        _ghostSnapped = false;
        if (!Input.IsKeyPressed(Key.Alt) && FindSnap(_current, pos, basis, out var snapped))
        {
            pos = snapped; _ghostSnapped = true;
        }
        else if (_gridSnap)
        {
            pos.X = Mathf.Snapped(pos.X, 0.5f); pos.Z = Mathf.Snapped(pos.Z, 0.5f);
        }
        _ghostPos = pos;
        _ghost.Transform = new Transform3D(basis, pos);
        _ghost.Visible = true;
        _ghostValid = true;
        var mat = PieceVisuals.GhostMaterial(_ghostSnapped);
        if (_current.Material != null)
        {
            // Preview the support this piece would get here.
            var gn = StabilityNode.Create(_current, pos, basis);
            float s = Stability.Evaluate(gn, _placed.Select(p => p.Node).ToList(), _cat.Materials);
            var m = _cat.Materials[_current.Material];
            mat = s < m.MinSupport ? GhostTint(new Color(1, 0.1f, 0.1f)) : GhostTint(Stability.HighlightColor(Stability.ColorValue(s, m)));
            _status.Text += "   " + (s < m.MinSupport ? Loc.T("Here: the piece would fall") : Loc.F("Here: stability {0:0}/{1:0}", s, m.MaxSupport));
        }
        foreach (var mi in _ghostMeshes) mi.MaterialOverride = mat;
    }

    readonly Dictionary<int, StandardMaterial3D> _ghostTints = new();
    StandardMaterial3D GhostTint(Color c)
    {
        int key = ((int)(c.R * 31) << 10) | ((int)(c.G * 31) << 5) | (int)(c.B * 31);
        if (!_ghostTints.TryGetValue(key, out var m))
            _ghostTints[key] = m = new StandardMaterial3D
            {
                AlbedoColor = new Color(c, 0.5f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
        return m;
    }

    // ------------------------------------------------------------------ stability
    void Restabilize()
    {
        if (_loading) return;
        Stability.Solve(_placed.Select(p => p.Node).ToList(), _cat.Materials);
        _selection.RemoveWhere(p => !_placed.Contains(p));
        if (_guide) { RebuildGuide(); RefreshGuide(); }
        ApplyColors();
        Autosave();
    }

    void ApplyColors()
    {
        foreach (var p in _placed)
        {
            bool above = _planMode && p.Level > _level, below = _planMode && p.Level < _level;
            p.Body.Visible = !above;
            p.Body.CollisionLayer = above ? 0u : 1u;
            if (below) { foreach (var mi in p.Meshes) mi.MaterialOverride = _fadedMat; continue; }
            // Rebuild guide: pieces still to come are faded, the current step glows gold, built ones look normal.
            var gs = StateOf(p);
            if (gs == GuideState.Todo) { foreach (var mi in p.Meshes) mi.MaterialOverride = _fadedMat; continue; }
            if (gs == GuideState.Current)
            {
                if (_planMode) { foreach (var mi in p.Meshes) mi.MaterialOverride = _selMat; continue; }
                var gm = PieceShader.MaterialFor(p.Def);
                foreach (var mi in p.Meshes) { mi.MaterialOverride = mi.HasMeta("own") ? null : gm; PieceShader.SetTint(mi, new Color(1f, 0.78f, 0.25f), 0.55f, 0.35f); }
                continue;
            }
            if (_selection.Contains(p) && _planMode) { foreach (var mi in p.Meshes) mi.MaterialOverride = _selMat; continue; }
            if (_planMode && !p.Node.Falls)
            {
                // one colour per piece type, matching the legend in the side panel
                if (!_typeColors.TryGetValue(p.Def, out var tc)) _typeColors[p.Def] = tc = Palette[_typeColors.Count % Palette.Length];
                if (!_typeMats.TryGetValue(tc, out var tm))
                    _typeMats[tc] = tm = new StandardMaterial3D { AlbedoColor = tc, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                foreach (var mi in p.Meshes) mi.MaterialOverride = tm;
                continue;
            }
            // 3D: textured material, with an overlay tint for selection / collapse / stability
            Color tint = Colors.White; float amt = 0f, emit = 0f;
            if (_selection.Contains(p)) { tint = new Color(1f, 0.8f, 0.15f); amt = 0.7f; emit = 0.25f; }
            else if (p.Node.Falls) { tint = new Color(1f, 0.08f, 0.05f); amt = 0.85f; emit = 0.4f; }
            else if (_showStability && p.Node.Participates)
            {
                var m = _cat.Materials[p.Def.Material!];
                tint = Stability.HighlightColor(Stability.ColorValue(p.Node.Support, m));
                amt = 0.6f;
            }
            var baseMat = PieceShader.MaterialFor(p.Def);
            foreach (var mi in p.Meshes) { mi.MaterialOverride = mi.HasMeta("own") ? null : baseMat; PieceShader.SetTint(mi, tint, amt, emit); }
        }
        UpdateTally();
    }

    /// <summary>
    /// Game rule (Player.UpdatePlacementGhost): the ghost is moved so that the point of its collision
    /// closest to the aimed point touches it — the piece rests on the surface instead of sinking into it.
    /// With a manual snap point chosen, that snap point is put on the aimed point instead.
    /// </summary>
    Vector3 RestOnSurface(PieceDef def, Basis basis, Vector3 point, Vector3 normal)
    {
        if (_manualSnap >= 0 && _manualSnap < def.Snaps.Count) return point - basis * def.Snaps[_manualSnap];
        var origin = point + normal * 50f;
        Vector3 best = origin; float bestD = float.MaxValue;
        foreach (var b in def.Boxes)
        {
            var c = ClosestPointOnBox(origin, basis, b, point);
            float d = c.DistanceSquaredTo(point);
            if (d < bestD) { bestD = d; best = c; }
        }
        return point + (origin - best);
    }

    static Vector3 ClosestPointOnBox(Vector3 piecePos, Basis pieceBasis, BoxDef box, Vector3 q)
    {
        var axes = pieceBasis * new Basis(box.Rotation);
        var center = piecePos + pieceBasis * box.Center;
        var local = axes.Inverse() * (q - center);
        var h = box.Size / 2;
        local = local.Clamp(-h, h);
        return center + axes * local;
    }

    /// <summary>
    /// Game rule (Player.FindClosestSnapPoints): among pieces within 10 m, find the (ghost snap, placed snap)
    /// pair closer than 0.5 m and move the ghost so they coincide. Refused if an identical piece already sits there.
    /// </summary>
    bool FindSnap(PieceDef def, Vector3 pos, Basis basis, out Vector3 result)
    {
        result = pos;
        if (def.Snaps.Count == 0) return false;
        float best = float.MaxValue;
        bool found = false;
        foreach (var p in _placed)
        {
            if (p.Def.Snaps.Count == 0 || p.Pos.DistanceTo(pos) > SnapSearchRadius) continue;
            var pb = new Basis(Vector3.Up, Mathf.DegToRad(p.Rot * RotStepDeg));
            foreach (var ps in p.Def.Snaps)
            {
                var w = p.Pos + pb * ps;
                for (int i = 0; i < def.Snaps.Count; i++)
                {
                    if (_manualSnap >= 0 && i != _manualSnap) continue;
                    var g = pos + basis * def.Snaps[i];
                    float d = g.DistanceTo(w);
                    if (d <= SnapRadius && d < best) { best = d; result = pos + (w - g); found = true; }
                }
            }
        }
        var target = result;
        if (found && _placed.Any(p => p.Def == def && p.Pos.DistanceTo(target) < 0.05f))
        {
            result = pos;
            return false;
        }
        return found;
    }

    // ------------------------------------------------------------------ editing
    Placed AddPiece(PieceDef def, Vector3 pos, int rot)
    {
        var body = PieceVisuals.BuildSolid(def);
        var basis = new Basis(Vector3.Up, Mathf.DegToRad(rot * RotStepDeg));
        body.Transform = new Transform3D(basis, pos);
        _world.AddChild(body);
        var p = new Placed { Def = def, Body = body, Pos = pos, Rot = rot, Node = StabilityNode.Create(def, pos, basis) };
        p.Level = LevelOf(p.Node);
        p.Meshes.AddRange(body.GetChildren().OfType<MeshInstance3D>());
        _placed.Add(p);
        _byBody[body.GetInstanceId()] = p;
        return p;
    }

    void Remove(Placed p)
    {
        _placed.Remove(p);
        _byBody.Remove(p.Body.GetInstanceId());
        p.Body.QueueFree();
    }

    void PlaceCurrent()
    {
        var p = AddPiece(_current!, _ghostPos, _rot);
        PushUndo(new() { (true, p) });
        Restabilize();
    }

    void RemoveHovered()
    {
        if (_selection.Count > 0) { DeleteSelection(); return; }
        if (_hovered == null) return;
        var p = _hovered;
        Remove(p);
        PushUndo(new() { (false, p) });
        _hovered = null;
        Restabilize();
    }

    void PushUndo(List<(bool Added, Placed P)> batch)
    {
        if (batch.Count == 0) return;
        _undo.Add(batch);
        if (_undo.Count > 500) _undo.RemoveAt(0);
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        var batch = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            var (added, p) = batch[i];
            if (added) { if (_placed.Contains(p)) Remove(p); }
            else AddPiece(p.Def, p.Pos, p.Rot);
        }
        _selection.Clear();
        Restabilize();
    }

    void ClearAll()
    {
        foreach (var p in _placed.ToList()) Remove(p);
        _undo.Clear();
        _selection.Clear();
        Restabilize();
    }

    // ------------------------------------------------------------------ selection & copy/paste
    readonly HashSet<Placed> _selection = new();
    List<(PieceDef Def, Vector3 Off, int Rot)>? _clipboard;
    bool _pasting, _pasteValid;
    Node3D? _pasteGhost;
    List<MeshInstance3D> _pasteMeshes = new();
    Vector3 _pastePos;
    StandardMaterial3D _selMat = null!;
    bool _rectSel;
    Vector2 _rectStart;
    ColorRect _rectView = null!;

    void ToggleSelect(Placed p)
    {
        if (!_selection.Remove(p)) _selection.Add(p);
        ApplyColors();
    }

    void SelectAllVisible()
    {
        _selection.Clear();
        foreach (var p in _placed) if (!_planMode || p.Level == _level) _selection.Add(p);
        ApplyColors();
    }

    void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        ApplyColors();
    }

    void DeleteSelection()
    {
        var batch = _selection.Where(_placed.Contains).Select(p => (false, p)).ToList();
        foreach (var (_, p) in batch) Remove(p);
        _selection.Clear();
        PushUndo(batch);
        Restabilize();
    }

    void CopySelection()
    {
        if (_selection.Count == 0) { _status.Text = Loc.T("Nothing selected (Ctrl+click to select)"); return; }
        var anchor = _selection.OrderBy(p => p.Level).ThenBy(p => p.Pos.Y).ThenBy(p => p.Pos.X).ThenBy(p => p.Pos.Z).First();
        _clipboard = new() { (anchor.Def, Vector3.Zero, anchor.Rot) };
        foreach (var p in _selection) if (p != anchor) _clipboard.Add((p.Def, p.Pos - anchor.Pos, p.Rot));
        _flash = Loc.N("{0} piece copied — Ctrl+V to paste", "{0} pieces copied — Ctrl+V to paste", _clipboard.Count);
        _flashUntil = Time.GetTicksMsec() + 2500;
    }

    void StartPaste()
    {
        if (_clipboard == null || _clipboard.Count == 0) return;
        SelectPiece(null);
        CancelPaste();
        _pasting = true;
        _rot = 0;
        _manualSnap = -1;
        _pasteGhost = new Node3D { Name = "PasteGhost" };
        _pasteMeshes = new();
        foreach (var (def, off, rot) in _clipboard)
        {
            var g = PieceVisuals.BuildGhost(def, out var meshes);
            g.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(rot * RotStepDeg)), off);
            _pasteGhost.AddChild(g);
            _pasteMeshes.AddRange(meshes);
        }
        AddChild(_pasteGhost);
    }

    void CancelPaste()
    {
        _pasting = false;
        _pasteGhost?.QueueFree();
        _pasteGhost = null;
    }

    void UpdatePaste((Vector3 pos, Vector3 normal, Placed? hit)? hit)
    {
        if (_pasteGhost == null || _clipboard == null) return;
        if (hit == null) { _pasteGhost.Visible = false; _pasteValid = false; return; }
        var (anchorDef, _, anchorRot) = _clipboard[0];
        var groupBasis = new Basis(Vector3.Up, Mathf.DegToRad(_rot * RotStepDeg));
        var anchorBasis = new Basis(Vector3.Up, Mathf.DegToRad(((anchorRot + _rot) % 16) * RotStepDeg));
        var pos = RestOnSurface(anchorDef, anchorBasis, hit.Value.pos, hit.Value.normal);
        bool snapped = false;
        if (!Input.IsKeyPressed(Key.Alt) && FindSnap(anchorDef, pos, anchorBasis, out var s)) { pos = s; snapped = true; }
        _pastePos = pos;
        _pasteGhost.Transform = new Transform3D(groupBasis, pos);
        _pasteGhost.Visible = true;
        _pasteValid = true;
        var mat = PieceVisuals.GhostMaterial(snapped);
        foreach (var m in _pasteMeshes) m.MaterialOverride = mat;
        _status.Text += "   " + Loc.N("Pasting {0} piece — click: place · wheel/R: rotate · Esc: stop", "Pasting {0} pieces — click: place · wheel/R: rotate · Esc: stop", _clipboard.Count);
    }

    void PlacePaste()
    {
        if (_clipboard == null || !_pasteValid) return;
        var groupBasis = new Basis(Vector3.Up, Mathf.DegToRad(_rot * RotStepDeg));
        var batch = new List<(bool, Placed)>();
        foreach (var (def, off, rot) in _clipboard)
            batch.Add((true, AddPiece(def, _pastePos + groupBasis * off, (rot + _rot) % 16)));
        PushUndo(batch);
        Restabilize();
    }

    void FinishRectSelect(Vector2 end, bool add)
    {
        _rectSel = false;
        _rectView.Visible = false;
        var r = new Rect2(_rectStart, end - _rectStart).Abs();
        if (!add) _selection.Clear();
        foreach (var p in _placed)
            if (p.Level == _level && r.HasPoint(_planCam.UnprojectPosition(p.Node.Com))) _selection.Add(p);
        ApplyColors();
    }

    // ------------------------------------------------------------------ files
    sealed record PlanPiece(string Prefab, float[] Pos, int Rot);
    sealed record PlanFile(string Format, List<PlanPiece> Pieces);
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    const string AutosavePath = "user://autosave.vplan.json";
    bool _testMode, _loading;
    string _flash = "";
    ulong _flashUntil;

    void SavePlan(string path, bool silent = false)
    {
        var plan = new PlanFile("valheim-planner/1", _placed.Select(p => new PlanPiece(p.Def.Prefab, new[] { p.Pos.X, p.Pos.Y, p.Pos.Z }, p.Rot)).ToList());
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) { GD.PushWarning($"Impossible d'écrire {path}"); return; }
        f.StoreString(JsonSerializer.Serialize(plan, JsonOpts));
        if (!silent) { _flash = Loc.F("Plan saved: {0}", path); _flashUntil = Time.GetTicksMsec() + 3000; }
    }

    void Autosave()
    {
        if (_testMode || _loading) return;
        SavePlan(AutosavePath, silent: true);
    }

    void LoadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<PlanFile>(FileAccess.GetFileAsString(path), JsonOpts);
        if (plan == null) return;
        _loading = true;
        ClearAll();
        int missing = 0;
        foreach (var pp in plan.Pieces)
        {
            if (!_cat.ByPrefab.TryGetValue(pp.Prefab, out var def)) { missing++; continue; }
            AddPiece(def, new Vector3(pp.Pos[0], pp.Pos[1], pp.Pos[2]), pp.Rot);
        }
        _loading = false;
        Restabilize();
        if (missing > 0) GD.PushWarning($"{missing} pièces inconnues ignorées");
    }

    void ExportList(string path)
    {
        var text = BuildSteps.Export(_placed.Select(p => (p.Def, p.Level)), LevelHeight);
        System.IO.File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        OS.ShellOpen(path);
    }

    // ------------------------------------------------------------------ testing helpers
    /// <summary>Builds a small house the way a player would: each piece is dropped near its spot and snapping does the rest.</summary>
    void BuildDemo()
    {
        var floor = _cat.ByPrefab["wood_floor"];
        var wall = _cat.ByPrefab["woodwall"];
        var pole = _cat.ByPrefab["wood_pole2"];
        var beam = _cat.ByPrefab["wood_beam"];
        var roof = _cat.ByPrefab["wood_roof"];

        Vector3 Drop(PieceDef d, Vector3 approx, int rot)
        {
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(rot * RotStepDeg));
            return FindSnap(d, approx, basis, out var s) ? s : approx;
        }

        AddPiece(floor, Vector3.Zero, 0);
        for (int x = 0; x < 3; x++)
            for (int z = 0; z < 2; z++)
                if (x + z > 0) AddPiece(floor, Drop(floor, new Vector3(x * 2 + 0.2f, 0, -z * 2 - 0.2f), 0), 0);
        // walls on the front and back edges, sides rotated 90°
        for (int x = 0; x < 3; x++)
        {
            AddPiece(wall, Drop(wall, new Vector3(x * 2, 0.9f, 1.1f), 0), 0);
            if (x != 1) AddPiece(wall, Drop(wall, new Vector3(x * 2, 0.9f, -3.1f), 0), 0);
        }
        for (int z = 0; z < 2; z++)
        {
            AddPiece(wall, Drop(wall, new Vector3(-1.1f, 0.9f, -z * 2), 4), 4);
            AddPiece(wall, Drop(wall, new Vector3(5.1f, 0.9f, -z * 2), 4), 4);
        }
        // corner poles
        foreach (var c in new[] { new Vector3(-1, 1, 1), new Vector3(5, 1, 1), new Vector3(-1, 1, -3), new Vector3(5, 1, -3) })
            AddPiece(pole, Drop(pole, c + new Vector3(0.1f, 0, 0.1f), 0), 0);
        // ridge beam (raised 1 m) and a 26° roof on each side: low edge on the walls, high edge on the ridge
        for (int x = 0; x < 3; x++)
            AddPiece(beam, new Vector3(x * 2, 3, -1), 0);
        for (int x = 0; x < 3; x++)
        {
            AddPiece(roof, Drop(roof, new Vector3(x * 2, 2.1f, 0.1f), 8), 8);
            AddPiece(roof, Drop(roof, new Vector3(x * 2, 2.1f, -2.1f), 0), 0);
        }
        // gable walls on both sides, filled triangles
        var gable = _cat.ByPrefab["wood_wall_roof_a"];
        foreach (var x in new[] { -1f, 5f })
        {
            AddPiece(gable, Drop(gable, new Vector3(x, 2.1f, 0.1f), 4), 4);
            AddPiece(gable, Drop(gable, new Vector3(x, 2.1f, -2.1f), 12), 12);
        }
        // door in the back gap, placed exactly like a player would: aim at the floor near the edge
        var door = _cat.ByPrefab["wood_door"];
        var floorTop = floor.Boxes.Max(b => b.Center.Y + b.Size.Y / 2);
        var basisDoor = Basis.Identity;
        var rest = RestOnSurface(door, basisDoor, new Vector3(2.3f, floorTop, -2.75f), Vector3.Up);
        AddPiece(door, FindSnap(door, rest, basisDoor, out var ds) ? ds : rest, 0);
        Restabilize();
    }

    string StabilityCases()
    {
        var sb = new StringBuilder("STABILITY\n");
        void Report(string title)
        {
            Restabilize();
            sb.AppendLine($"-- {title}");
            foreach (var p in _placed)
                sb.AppendLine($"   {p.Def.Prefab,-18} ({p.Pos.X:0.##},{p.Pos.Y:0.##},{p.Pos.Z:0.##}) support={p.Node.Support:0.0}{(p.Node.Grounded ? " ground" : "")}{(p.Node.Falls ? " FALLS" : "")}");
            ClearAll();
        }
        PieceDef P(string n) => _cat.ByPrefab[n];

        for (int i = 0; i < 10; i++) AddPiece(P("wood_pole2"), new Vector3(0, 1 + 2 * i, 0), 0);
        Report("wood_pole2 stack");
        for (int i = 0; i < 8; i++) AddPiece(P("wood_pole_log_4"), new Vector3(0, 2 + 4 * i, 0), 0);
        Report("core wood 4m stack");
        AddPiece(P("wood_pole2"), new Vector3(0, 1, 0), 0);
        for (int i = 0; i < 6; i++) AddPiece(P("wood_beam"), new Vector3(1 + 2 * i, 2, 0), 0);
        Report("wood beam cantilever from a pole");
        AddPiece(P("stone_wall_2x1"), new Vector3(0, 0.5f, 0), 0);
        AddPiece(P("stone_wall_2x1"), new Vector3(0, 1.5f, 0), 0);
        AddPiece(P("stone_wall_2x1"), new Vector3(2, 1.5f, 0), 0);
        Report("stone: stack + sideways");
        AddPiece(P("wood_pole2"), new Vector3(0, 1, 0), 0);
        AddPiece(P("wood_pole2"), new Vector3(4, 1, 0), 0);
        AddPiece(P("wood_beam"), new Vector3(1, 2, 0), 0);
        AddPiece(P("wood_beam"), new Vector3(3, 2, 0), 0);
        AddPiece(P("wood_pole2"), new Vector3(2, 3, 0), 0);
        Report("bridge of 2 beams between poles + pole in the middle");
        return sb.ToString();
    }

    void SelfTest()
    {
        GD.Print(StabilityCases());
        BuildDemo();
        Restabilize();
        int before = _placed.Count;
        SelectAllVisible(); CopySelection(); StartPaste();
        _pastePos = new Vector3(12, 0, 0); _pasteValid = true; _rot = 4; PlacePaste();
        int afterPaste = _placed.Count;
        int fallsAfterPaste = _placed.Count(p => p.Node.Falls);
        Undo();
        CancelPaste(); ClearSelection();
        GD.Print($"PASTE: before={before} afterPaste={afterPaste} falls={fallsAfterPaste} afterUndo={_placed.Count}");

        // PlanBuild round trip: parse the blueprint back like PlanBuild would and compare positions/rotations.
        var bp = PlanBuildExport.Build("Test", _placed.Select(p => (p.Def, p.Pos, p.Rot)), RotStepDeg);
        var lines = bp.Split('\n').SkipWhile(l => l != "#Pieces").Skip(1).Where(l => l.Length > 0).ToList();
        var minU = new Vector3(_placed.Min(p => p.Pos.X), _placed.Min(p => p.Pos.Y), _placed.Min(p => -p.Pos.Z));
        int good = 0;
        foreach (var line in lines)
        {
            var f = line.Split(';');
            float P(int i) => float.Parse(f[i], System.Globalization.CultureInfo.InvariantCulture);
            var unityPos = new Vector3(P(2), P(3), P(4)) + minU;
            var godotPos = new Vector3(unityPos.X, unityPos.Y, -unityPos.Z);
            float unityAngle = Mathf.RadToDeg(2 * Mathf.Atan2(P(6), P(8)));
            int rot = ((int)Mathf.Round(-unityAngle / RotStepDeg) % 16 + 16) % 16;
            if (_placed.Any(p => p.Def.Prefab == f[0] && p.Pos.DistanceTo(godotPos) < 0.002f && p.Rot == rot) && f.Length == 13) good++;
        }
        GD.Print($"PLANBUILD: {lines.Count} lines, {good} match the plan exactly\n" + string.Join("\n", bp.Split('\n').Take(8)));

        SetGuide(true);
        GD.Print($"GUIDE: {_gSteps.Count} steps; first = {string.Join(", ", _gSteps[0].GroupBy(p => p.Def.Prefab).Select(g => g.Count() + "x" + g.Key))}; last = {string.Join(", ", _gSteps[^1].GroupBy(p => p.Def.Prefab).Select(g => g.Count() + "x" + g.Key))}");
        _gMode.ButtonPressed = true;
        GD.Print($"GUIDE piece-by-piece: {_gSteps.Count} steps (pieces: {_placed.Count})");
        _gMode.ButtonPressed = false;
        SetGuide(false);

        // Import round trip: export → clear → import, then compare the layout (up to a global shift).
        var snapshot = _placed.Select(p => (p.Def.Prefab, p.Pos, p.Rot)).ToList();
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vp_roundtrip.blueprint");
        System.IO.File.WriteAllText(tmp, bp + "modded_piece_xyz;Misc;0;0;0;0;0;0;1;\"\";1;1;1\n");
        string tmpV = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vp_roundtrip.vbuild");
        System.IO.File.WriteAllText(tmpV, string.Join("\n", _placed.Select(p =>
        {
            float h = Mathf.DegToRad(-p.Rot * RotStepDeg) / 2;
            string N(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{p.Def.Prefab} 0 {N(Mathf.Sin(h))} 0 {N(Mathf.Cos(h))} {N(p.Pos.X + 100)} {N(p.Pos.Y + 30)} {N(-p.Pos.Z + 50)}";
        })));
        foreach (var file in new[] { tmp, tmpV })
        {
            ClearAll();
            ImportBlueprint(file);
            var first = _placed.First(p => p.Def.Prefab == snapshot[0].Prefab && p.Rot == snapshot[0].Rot);
            var offset = first.Pos - snapshot[0].Pos;
            int same = snapshot.Count(s => _placed.Any(p => p.Def.Prefab == s.Prefab && p.Rot == s.Rot && p.Pos.DistanceTo(s.Pos + offset) < 0.002f));
            GD.Print($"IMPORT {System.IO.Path.GetExtension(file)}: {_placed.Count} pieces, {same}/{snapshot.Count} identical layout · \"{_flash}\"");
        }
        ClearAll();
        BuildDemo();
        Restabilize();
        GD.Print(BuildSteps.Export(_placed.Select(p => (p.Def, p.Level)), LevelHeight));
        var sb = new StringBuilder("SELFTEST\n");
        foreach (var p in _cat.Pieces.Where(p => p.Triangle != null)) sb.Append(p.Prefab).Append(' ');
        sb.AppendLine();
        foreach (var p in _placed) sb.AppendLine($"  {p.Def.Prefab,-12} pos=({p.Pos.X:0.###}, {p.Pos.Y:0.###}, {p.Pos.Z:0.###}) rot={p.Rot}");
        sb.AppendLine(_tally.Text);
        GD.Print(sb.ToString());
    }

    async System.Threading.Tasks.Task RunScreenshot(string path)
    {
        if (OS.GetCmdlineUserArgs().Contains("--objects"))
        {
            string[] names = OS.GetCmdlineUserArgs().Contains("--set2")
                ? new[] { "fire_pit", "hearth", "smelter", "charcoal_kiln", "piece_groundtorch_wood", "piece_artisanstation", "piece_spinningwheel", "blastfurnace", "eitrrefinery", "piece_snowlantern", "fermenter" }
                : new[] { "piece_workbench", "piece_workbench_ext1", "piece_workbench_ext2", "forge", "forge_ext1", "forge_ext2",
                          "piece_chest_wood", "bed", "piece_chair", "piece_table", "piece_stonecutter", "piece_cauldron", "piece_bench01", "piece_spinningwheel" };
            float x = 0;
            foreach (var n in names)
            {
                if (!_cat.ByPrefab.TryGetValue(n, out var d)) { GD.Print($"missing {n}"); continue; }
                AddPiece(d, new Vector3(x, 0.05f, 0), 8);
                x += Mathf.Clamp(d.Radius * 1.4f, 2.2f, 4.5f);
            }
            Restabilize();
            SelectPiece(null);
            _cam.Position = new Vector3(x * 0.5f, 5f, Mathf.Min(x * 0.42f + 2f, 16f));
            _yaw = 0; _pitch = Mathf.DegToRad(-24); ApplyCamRotation();
            _cam.Fov = 75;
            for (int i = 0; i < 20; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GetViewport().GetTexture().GetImage().SavePng(path);
            GetTree().Quit();
            return;
        }
        var loadArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--load="));
        if (loadArg != null)
        {
            LoadPlan(loadArg["--load=".Length..]);
            // frame the plan from a 3/4 view, from the side given by --view=N (0..3)
            var c = _placed.Aggregate(Vector3.Zero, (acc, p) => acc + p.Pos) / Math.Max(1, _placed.Count);
            float r = _placed.Count == 0 ? 8 : _placed.Max(p => p.Pos.DistanceTo(c)) + 4;
            var viewArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--view="));
            int view = viewArg != null ? int.Parse(viewArg["--view=".Length..]) : 0;
            float ang = Mathf.DegToRad(-40 + 90 * view);
            _yaw = ang; _pitch = Mathf.DegToRad(-28);
            ApplyCamRotation();
            _cam.Position = c - _cam.GlobalTransform.Basis.Z * -(r * 1.6f);
            SelectPiece(null);
            for (int i = 0; i < 20; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GetViewport().GetTexture().GetImage().SavePng(path);
            GetTree().Quit();
            return;
        }
        BuildDemo();
        if (OS.GetCmdlineUserArgs().Contains("--stab"))
        {
            // a too-tall tower and a too-long cantilever next to the house, to show the colours
            for (int i = 0; i < 9; i++) AddPiece(_cat.ByPrefab["wood_pole2"], new Vector3(-4, 1 + 2 * i, -1), 0);
            for (int i = 0; i < 5; i++) AddPiece(_cat.ByPrefab["wood_beam"], new Vector3(-3 + 2 * i, 16, -1), 0);
            Restabilize();
            _stabBtn.ButtonPressed = true;
        }
        bool back = OS.GetCmdlineUserArgs().Contains("--back");
        if (!back) SelectPiece(_cat.ByPrefab["woodwall"]);
        bool stab = OS.GetCmdlineUserArgs().Contains("--stab");
        _cam.Position = back ? new Vector3(-5, 5, -10) : stab ? new Vector3(-14, 9, 16) : new Vector3(-7, 6.5f, 8);
        _yaw = Mathf.DegToRad(back ? 217 : stab ? -25 : -40); _pitch = Mathf.DegToRad(back ? -22 : stab ? -8 : -27);
        ApplyCamRotation();
        var guideArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--guide="));
        if (guideArg != null)
        {
            SelectPiece(null);
            SetGuide(true);
            GuideGo(int.Parse(guideArg["--guide=".Length..]));
            GuideFocus();
        }
        if (OS.GetCmdlineUserArgs().Contains("--pbdialog")) { SelectPiece(null); SetFreeCursor(true); OpenPlanBuild(); }
        var planArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--plan="));
        if (planArg != null)
        {
            SelectPiece(null);
            SetPlanMode(true);
            ChangeLevel(int.Parse(planArg["--plan=".Length..]));
        }
        for (int i = 0; i < 20 || (_thumbs.Busy && i < 3000); i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
        GD.Print($"Screenshot saved: {path}");
        GetTree().Quit();
    }
}