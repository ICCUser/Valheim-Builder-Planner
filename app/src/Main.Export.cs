using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace ValheimPlanner;

/// <summary>Export menu and the optional "send to PlanBuild" window.</summary>
public partial class Main
{
    MenuButton _exportMenu = null!;
    ConfirmationDialog _pbDlg = null!;
    LineEdit _pbName = null!;
    VBoxContainer _pbTargets = null!;
    Label _pbStatus = null!;
    FileDialog _pbSaveDlg = null!;
    readonly List<(CheckBox Box, string Dir)> _pbChecks = new();

    MenuButton BuildExportMenu(Control layer)
    {
        _exportMenu = new MenuButton { Text = "Export ▾", FocusMode = Control.FocusModeEnum.None, Flat = false };
        _exportMenu.AddThemeStyleboxOverride("normal", GetTree().Root.Theme.GetStylebox("normal", "Button"));
        _exportMenu.AddThemeStyleboxOverride("hover", GetTree().Root.Theme.GetStylebox("hover", "Button"));
        _exportMenu.AddThemeStyleboxOverride("pressed", GetTree().Root.Theme.GetStylebox("pressed", "Button"));
        FillExportMenu();
        _exportMenu.GetPopup().IdPressed += id =>
        {
            if (_placed.Count == 0) return;
            SetFreeCursor(true);
            if (id == 0) _exportDlg.PopupCentered(new Vector2I(900, 600));
            else OpenPlanBuild();
        };
        BuildPlanBuildDialog(layer);
        return _exportMenu;
    }

    void FillExportMenu()
    {
        var pop = _exportMenu.GetPopup();
        pop.Clear();
        pop.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        pop.AddItem(Loc.T("Build list (text)…"), 0);
        pop.AddItem(Loc.T("PlanBuild blueprint (mod, optional)…"), 1);
    }

    void BuildPlanBuildDialog(Control layer)
    {
        _pbDlg = new ConfirmationDialog { Title = "Send to PlanBuild", OkButtonText = "Send to PlanBuild", CancelButtonText = "Close", MinSize = new Vector2I(620, 0) };
        _pbDlg.GetOkButton().CustomMinimumSize = new Vector2(180, 0);
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 10);
        _pbDlg.AddChild(vb);
        vb.AddChild(new Label
        {
            Text = "PlanBuild is a Valheim mod: it shows this plan as ghost pieces in your world, which you then build with your own materials.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(640, 0),
        });
        var warn = new Label
        {
            Text = "While a mod is loaded, Valheim turns achievements off. Nothing is written to your character or world: without the mod, achievements work again.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(640, 0),
        };
        warn.AddThemeColorOverride("font_color", UiTheme.Gold);
        vb.AddChild(warn);
        var nameRow = new HBoxContainer();
        nameRow.AddChild(new Label { Text = "Blueprint name:" });
        _pbName = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        nameRow.AddChild(_pbName);
        vb.AddChild(nameRow);
        _pbTargets = new VBoxContainer();
        vb.AddChild(_pbTargets);
        _pbStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(640, 0), AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        vb.AddChild(_pbStatus);

        var save = _pbDlg.AddButton("Save the file…", false, "save");
        _pbDlg.CustomAction += action => { if (action == "save") _pbSaveDlg.PopupCentered(new Vector2I(900, 600)); };
        _pbDlg.GetOkButton().Pressed += SendToPlanBuild;
        _pbDlg.DialogHideOnOk = false;
        layer.AddChild(_pbDlg);

        _pbSaveDlg = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, Title = "Save the file…", UseNativeDialog = true };
        _pbSaveDlg.AddFilter("*.blueprint", "PlanBuild");
        _pbSaveDlg.FileSelected += path =>
        {
            File.WriteAllText(path, BlueprintText());
            _pbStatus.Text = Loc.F("Saved: {0}", path);
        };
        layer.AddChild(_pbSaveDlg);
    }

    string BlueprintText() =>
        PlanBuildExport.Build(_pbName.Text.Trim().Length > 0 ? _pbName.Text.Trim() : AppInfo.Name, _placed.Select(p => (p.Def, p.Pos, p.Rot)), RotStepDeg);

    void OpenPlanBuild()
    {
        if (_pbName.Text.Length == 0) _pbName.Text = Loc.T("My build");
        foreach (var c in _pbTargets.GetChildren()) c.QueueFree();
        _pbChecks.Clear();
        var game = GameData.GameExtractor.ReadInfo(Setup.DataDir)?.GamePath;
        var targets = PlanBuildExport.Targets(game);
        bool any = targets.Any(t => t.HasPlanBuild);
        var title = new Label { Text = any ? Loc.T("Send to:") : Loc.T("PlanBuild was not found. Install it (with BepInEx and Jötunn) from Thunderstore or Nexus Mods, or save the file and copy it yourself."), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(640, 0), AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _pbTargets.AddChild(title);
        foreach (var (label, dir, has) in targets.Where(t => t.HasPlanBuild))
        {
            var cb = new CheckBox { Text = label + "  — " + Loc.T("PlanBuild found"), ButtonPressed = true, AutoTranslateMode = AutoTranslateModeEnum.Disabled, TooltipText = dir };
            _pbTargets.AddChild(cb);
            _pbChecks.Add((cb, dir));
        }
        _pbDlg.GetOkButton().Disabled = !any;
        _pbStatus.Text = "";
        _pbSaveDlg.CurrentFile = PlanBuildExport.SafeFileName(_pbName.Text) + ".blueprint";
        _pbDlg.PopupCentered(new Vector2I(680, 400));
    }

    static bool IsBlueprintFile(string path) =>
        path.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens any supported file: our plans, PlanBuild blueprints, BuildShare vbuilds.</summary>
    void OpenAnyFile(string path)
    {
        if (IsBlueprintFile(path)) ImportBlueprint(path);
        else if (path.EndsWith(".vplan.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) LoadPlan(path);
    }

    /// <summary>
    /// Community blueprints (Nexus, Thunderstore, PlanBuild players): placed directly into an empty plan,
    /// otherwise attached to the cursor like a paste so it can be put anywhere.
    /// </summary>
    void ImportBlueprint(string path)
    {
        PlanBuildExport.Imported imp;
        try { imp = PlanBuildExport.Parse(File.ReadAllText(path), path.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase), RotStepDeg); }
        catch (Exception e) { _flash = Loc.F("Could not read {0}: {1}", Path.GetFileName(path), e.Message); _flashUntil = Time.GetTicksMsec() + 6000; return; }

        var known = imp.Pieces.Where(p => _cat.ByPrefab.ContainsKey(p.Prefab)).Select(p => (Def: _cat.ByPrefab[p.Prefab], p.Pos, p.Rot)).ToList();
        int unknown = imp.Pieces.Count - known.Count;
        if (known.Count == 0)
        {
            _flash = Loc.T("This file contains no Valheim building pieces.");
            _flashUntil = Time.GetTicksMsec() + 6000;
            return;
        }
        // Rest the lowest piece on the ground and centre the build on the origin.
        float lowest = known.Min(k => k.Pos.Y + k.Def.Boxes.Min(b => b.Center.Y - b.Size.Y / 2));
        var center = known.Aggregate(Vector3.Zero, (a, k) => a + k.Pos) / known.Count;
        var shift = new Vector3(-center.X, -lowest, -center.Z);
        var placedList = known.Select(k => (k.Def, Pos: k.Pos + shift, k.Rot)).ToList();

        string name = imp.Name.Length > 0 ? imp.Name : Path.GetFileNameWithoutExtension(path);
        var msg = Loc.F("Imported \"{0}\"", name) + " · " + Loc.N("{0} piece", "{0} pieces", known.Count);
        if (unknown > 0) msg += " · " + Loc.N("{0} modded piece skipped", "{0} modded pieces skipped", unknown);
        if (imp.Tilted + imp.OffGrid > 0) msg += " · " + Loc.N("{0} rotation rounded to the game's steps", "{0} rotations rounded to the game's steps", imp.Tilted + imp.OffGrid);

        if (_placed.Count == 0)
        {
            var batch = placedList.Select(k => (true, AddPiece(k.Def, k.Pos, k.Rot))).ToList();
            PushUndo(batch);
            Restabilize();
            if (_planMode) FitPlanCamera();
            else
            {
                float r = placedList.Max(k => k.Pos.Length()) + 4;
                _cam.LookAtFromPosition(new Vector3(-0.6f, 0.55f, 0.85f).Normalized() * r * 1.8f, new Vector3(0, 2, 0), Vector3.Up);
                _yaw = _cam.Rotation.Y; _pitch = _cam.Rotation.X; ApplyCamRotation();
            }
        }
        else
        {
            var anchor = placedList.OrderBy(k => k.Pos.Y).ThenBy(k => k.Pos.X).ThenBy(k => k.Pos.Z).First();
            _clipboard = placedList.Select(k => (k.Def, k.Pos - anchor.Pos, k.Rot)).ToList();
            int a = _clipboard.FindIndex(c => c.Item2 == Vector3.Zero && c.Item1 == anchor.Def);
            if (a > 0) (_clipboard[0], _clipboard[a]) = (_clipboard[a], _clipboard[0]);
            if (!_planMode) SetFreeCursor(false);
            StartPaste();
            msg += " — " + Loc.T("click to place it");
        }
        _flash = msg;
        _flashUntil = Time.GetTicksMsec() + 8000;
    }

    void SendToPlanBuild()
    {
        var text = BlueprintText();
        string file = PlanBuildExport.SafeFileName(_pbName.Text) + ".blueprint";
        int ok = 0;
        foreach (var (box, dir) in _pbChecks.Where(c => c.Box.ButtonPressed))
        {
            try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, file), text); ok++; }
            catch (Exception e) { _pbStatus.Text = Loc.F("Could not write {0}: {1}", dir, e.Message); return; }
        }
        _pbStatus.Text = ok == 0 ? Loc.T("Tick at least one destination.")
            : Loc.F("Sent! In game: join your world again, take the Blueprint Rune and open its \"{0}\" tab. The plan appears as ghost pieces to build with your materials.", AppInfo.Name);
    }
}
