using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using ValheimPlanner.GameData;

namespace ValheimPlanner;

/// <summary>
/// First-launch screen: finds the player's Valheim install and reads the building pieces from it.
/// The planner ships without any game data; this is where it gets created, on the player's machine.
/// </summary>
public partial class Setup : CanvasLayer
{
    public static string DataDir => ProjectSettings.GlobalizePath("user://");

    readonly GameExtractor.Info? _old;
    readonly Action _done;
    string? _game;
    Label _text = null!, _path = null!, _step = null!;
    ProgressBar _bar = null!;
    Button _go = null!, _browse = null!, _keep = null!;
    FileDialog _dlg = null!;
    Task<GameExtractor.Info>? _task;
    volatile float _progress;
    volatile string _stepKey = "";

    public Setup() : this(null, () => { }) { }

    public Setup(GameExtractor.Info? old, Action done)
    {
        _old = old;
        _done = done;
    }

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        var bg = new ColorRect { Color = new Color(0.06f, 0.05f, 0.04f) };
        bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer { Theme = GetTree().Root.Theme };   // CanvasLayer does not pass the theme down
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(640, 0) };
        center.AddChild(panel);
        var m = new MarginContainer();
        foreach (var s in new[] { "left", "right", "top", "bottom" }) m.AddThemeConstantOverride("margin_" + s, 24);
        panel.AddChild(m);
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 14);
        m.AddChild(vb);

        vb.AddChild(new TextureRect { Texture = GD.Load<Texture2D>("res://icon.png"), CustomMinimumSize = new Vector2(0, 128), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
        var title = new Label { Text = AppInfo.Name.ToUpperInvariant(), AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeFontOverride("font", UiTheme.SerifBold);
        title.AddThemeColorOverride("font_color", UiTheme.Gold);
        vb.AddChild(title);

        _text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        vb.AddChild(_text);
        _path = new Label { AutowrapMode = TextServer.AutowrapMode.Arbitrary, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, Modulate = new Color(1, 0.85f, 0.55f) };
        vb.AddChild(_path);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        vb.AddChild(row);
        _go = new Button { Text = "Read my game", CustomMinimumSize = new Vector2(200, 40) };
        _go.Pressed += Start;
        _browse = new Button { Text = "Choose the Valheim folder…" };
        _browse.Pressed += () => _dlg.PopupCentered(new Vector2I(900, 600));
        _keep = new Button { Text = "Keep the old data", Visible = _old != null };
        _keep.Pressed += Finish;
        row.AddChild(_go); row.AddChild(_browse); row.AddChild(_keep);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, Visible = false, CustomMinimumSize = new Vector2(0, 22) };
        vb.AddChild(_bar);
        _step = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, Modulate = new Color(1, 1, 1, 0.75f) };
        vb.AddChild(_step);

        _dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Choose the Valheim folder…" };
        _dlg.DirSelected += dir =>
        {
            if (GameExtractor.IsValheimFolder(dir)) { _game = dir; Refresh(); }
            else _step.Text = Loc.T("This is not a Valheim folder (valheim_Data is missing).");
        };
        AddChild(_dlg);

        _game = _old != null && GameExtractor.IsValheimFolder(_old.GamePath) ? _old.GamePath : GameExtractor.FindInstalls().Count > 0 ? GameExtractor.FindInstalls()[0] : null;
        Refresh();
        if (OS.GetCmdlineUserArgs().Contains("--setup-auto") && _game != null) Start();
    }

    void Refresh()
    {
        _text.Text = (_old != null
                ? Loc.T("Valheim has changed since the planner last read it. Update the building pieces?")
                : Loc.T("Welcome! The planner reads the building pieces (shapes, sizes, costs, names) from your own Valheim installation."))
            + "\n\n" + Loc.T("Nothing from the game is included in the planner: this data is created on your computer and stays there. Your game and saves are only read, never modified.");
        _path.Text = _game != null ? Loc.F("Valheim folder: {0}", _game) : Loc.T("Valheim was not found automatically. Choose its folder (Steam: steamapps/common/Valheim).");
        _go.Disabled = _game == null;
    }

    void Start()
    {
        if (_game == null) return;
        _go.Disabled = _browse.Disabled = _keep.Disabled = true;
        _bar.Visible = true;
        string game = _game, outDir = DataDir;
        _task = Task.Run(() => GameExtractor.Run(game, outDir, (f, s) => { _progress = f; _stepKey = s; }));
    }

    int _frames;
    public override void _Process(double delta)
    {
        // Test hook: --setup-shot=<png> captures this screen (once at start, once while reading).
        var shot = System.Array.Find(OS.GetCmdlineUserArgs(), a => a.StartsWith("--setup-shot="));
        _frames++;
        if (shot != null && (_frames == 15 || _task != null && _progress > 0.5f && _frames < 100000))
        {
            var path = shot["--setup-shot=".Length..];
            if (_frames != 15) { path = path.Replace(".png", "_progress.png"); _frames = 100000; }
            GetViewport().GetTexture().GetImage().SavePng(path);
        }
        if (_task == null) return;
        _bar.Value = _progress;
        _step.Text = Loc.T(_stepKey) + "  " + Loc.T("(about a minute)");
        if (!_task.IsCompleted) return;
        if (_task.IsFaulted)
        {
            var err = _task.Exception?.GetBaseException().Message ?? "?";
            GD.PushError(err);
            _step.Text = Loc.F("Reading failed: {0}", err);
            _go.Disabled = _browse.Disabled = false;
            _keep.Disabled = _old == null;
            _task = null;
            return;
        }
        GD.Print($"Extracted {_task.Result.Pieces} pieces, {_task.Result.Shapes} shapes");
        Thumbs.ClearCache();   // pictures of the old pieces
        _task = null;
        Finish();
    }

    void Finish()
    {
        QueueFree();
        _done();
    }
}
