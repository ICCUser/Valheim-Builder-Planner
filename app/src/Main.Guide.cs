using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace ValheimPlanner;

/// <summary>
/// Rebuild guide (100 % vanilla): walks through the plan in build order — floor by floor, foundations first,
/// roof last — highlighting what to place now. Nothing is enforced: jump anywhere, go back, keep editing.
/// </summary>
public partial class Main
{
    bool _guide, _guidePieceByPiece;
    int _gStep;
    readonly List<List<Placed>> _gSteps = new();
    readonly Dictionary<Placed, int> _gIndex = new();

    Button _guideBtn = null!, _gPrev = null!, _gNext = null!, _gMode = null!;
    Control _guideBox = null!;
    RichTextLabel _gText = null!;
    ProgressBar _gBar = null!;

    enum GuideState { None, Done, Current, Todo }

    GuideState StateOf(Placed p) =>
        !_guide || !_gIndex.TryGetValue(p, out int i) ? GuideState.None
        : i < _gStep ? GuideState.Done : i == _gStep ? GuideState.Current : GuideState.Todo;

    /// <summary>Right-panel section, shown while the guide is on.</summary>
    Control BuildGuidePanel()
    {
        var box = new VBoxContainer { Visible = false };
        box.AddThemeConstantOverride("separation", 6);
        box.AddChild(UiTheme.Header("Rebuild guide"));
        _gBar = new ProgressBar { MinValue = 0, MaxValue = 1, CustomMinimumSize = new Vector2(0, 18), ShowPercentage = false };
        box.AddChild(_gBar);
        _gText = new RichTextLabel { AutoTranslateMode = AutoTranslateModeEnum.Disabled, BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _gText.AddThemeFontSizeOverride("normal_font_size", 14);
        _gText.AddThemeFontOverride("bold_font", UiTheme.SerifBold);
        box.AddChild(_gText);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        _gPrev = UiTheme.Btn("◀ Previous");
        _gNext = UiTheme.Btn("Next ▶");
        var look = UiTheme.Btn("Show");
        _gPrev.SizeFlagsHorizontal = _gNext.SizeFlagsHorizontal = look.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _gPrev.Pressed += () => GuideGo(_gStep - 1);
        _gNext.Pressed += () => GuideGo(_gStep + 1);
        look.Pressed += GuideFocus;
        row.AddChild(_gPrev); row.AddChild(look); row.AddChild(_gNext);
        box.AddChild(row);
        _gMode = UiTheme.Btn("One piece at a time", toggle: true);
        _gMode.Toggled += on => { _guidePieceByPiece = on; var cur = CurrentGuidePiece(); RebuildGuide(); if (cur != null) _gStep = _gIndex.GetValueOrDefault(cur); RefreshGuide(); };
        box.AddChild(_gMode);
        var hint = new Label { Text = "← → arrows: previous / next · click a piece: jump to it · you can keep editing", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", UiTheme.TextDim);
        box.AddChild(hint);
        _guideBox = box;
        return box;
    }

    void SetGuide(bool on)
    {
        _guide = on;
        if (_guideBtn.ButtonPressed != on) _guideBtn.SetPressedNoSignal(on);
        _guideBox.Visible = on;
        if (on)
        {
            RebuildGuide();
            _gStep = Math.Clamp(_gStep, 0, Math.Max(0, _gSteps.Count - 1));
            GuideFocus();
        }
        ApplyColors();
        RefreshGuide();
    }

    Placed? CurrentGuidePiece() => _gStep >= 0 && _gStep < _gSteps.Count ? _gSteps[_gStep].FirstOrDefault() : null;

    /// <summary>Floors bottom-up, then build steps (floors → poles → walls → … → roof), each walked spatially.</summary>
    void RebuildGuide()
    {
        _gSteps.Clear();
        _gIndex.Clear();
        Vector3 anchor = _placed.Count == 0 ? Vector3.Zero
            : new Vector3(_placed.Min(p => p.Pos.X), 0, _placed.Max(p => p.Pos.Z));
        foreach (var floor in _placed.GroupBy(p => p.Level).OrderBy(g => g.Key))
            foreach (var step in floor.GroupBy(p => BuildSteps.StepOf(p.Def)).OrderBy(g => g.Key))
            {
                // nearest-neighbour walk so consecutive pieces sit next to each other
                var left = step.ToList();
                var ordered = new List<Placed>();
                var at = anchor;
                while (left.Count > 0)
                {
                    var here = at;
                    var next = left.OrderBy(p => { var d = p.Pos - here; d.Y *= 3; return d.LengthSquared(); }).First(); // prefer same height
                    ordered.Add(next); left.Remove(next); at = next.Pos;
                }
                anchor = ordered[^1].Pos;
                if (_guidePieceByPiece) foreach (var p in ordered) _gSteps.Add(new List<Placed> { p });
                else _gSteps.Add(ordered);
            }
        for (int i = 0; i < _gSteps.Count; i++) foreach (var p in _gSteps[i]) _gIndex[p] = i;
        _gStep = Math.Clamp(_gStep, 0, Math.Max(0, _gSteps.Count - 1));
    }

    void GuideGo(int step)
    {
        if (_gSteps.Count == 0) return;
        _gStep = Math.Clamp(step, 0, _gSteps.Count - 1);
        ApplyColors();
        RefreshGuide();
        if (_planMode) GuideFocus();
    }

    /// <summary>Bring the current step into view (3D: look at it; floor view: switch to its floor).</summary>
    void GuideFocus()
    {
        if (_gStep >= _gSteps.Count) return;
        var group = _gSteps[_gStep];
        var c = group.Aggregate(Vector3.Zero, (a, p) => a + p.Node.Com) / group.Count;
        float r = Mathf.Max(3f, group.Max(p => p.Pos.DistanceTo(c)) + 2f);
        if (_planMode)
        {
            _level = group[0].Level;
            _planCam.Position = new Vector3(c.X, _planCam.Position.Y, c.Z);
            ApplyColors();
            UpdatePlanLabel();
            return;
        }
        var from = c + new Vector3(-0.55f, 0.6f, 0.8f).Normalized() * r * 2.2f;
        _cam.LookAtFromPosition(from, c, Vector3.Up);
        _yaw = _cam.Rotation.Y; _pitch = _cam.Rotation.X;
        ApplyCamRotation();
    }

    void RefreshGuide()
    {
        if (!_guide) return;
        _gPrev.Disabled = _gStep <= 0;
        _gNext.Disabled = _gStep >= _gSteps.Count - 1;
        var sb = new StringBuilder();
        if (_gSteps.Count == 0)
        {
            sb.Append(Loc.T("Nothing to rebuild yet: place some pieces first."));
            _gBar.Value = 0;
            _gText.Text = sb.ToString();
            return;
        }
        var group = _gSteps[_gStep];
        int doneCount = _gSteps.Take(_gStep).Sum(g => g.Count);
        _gBar.Value = (double)doneCount / _placed.Count;
        string gold = UiTheme.Gold.ToHtml(false);
        sb.AppendLine($"[color=#{gold}][b]" + Loc.F("Step {0} / {1}", _gStep + 1, _gSteps.Count) + "[/b][/color]   "
                      + Loc.F("Floor {0}", group[0].Level + 1) + " · " + Loc.T(BuildSteps.Names[BuildSteps.StepOf(group[0].Def)]));
        foreach (var g in group.GroupBy(p => p.Def))
            sb.AppendLine($"  {g.Count(),3} × {g.Key.Label}   [color=#{UiTheme.TextDim.ToHtml(false)}]({CostText(g.Key, g.Count())})[/color]");
        sb.AppendLine(Loc.F("Placed so far: {0} / {1}", doneCount, _placed.Count));
        var rest = _gSteps.Skip(_gStep).SelectMany(g => g).SelectMany(p => p.Def.Costs)
            .GroupBy(c => c.Label).Select(g => $"{g.Sum(c => c.Amount)} {g.Key}");
        sb.Append(Loc.F("Still to gather: {0}", string.Join(", ", rest)));
        _gText.Text = sb.ToString().Replace("\r\n", "\n");
    }

    static string CostText(PieceDef p, int count) => string.Join(", ", p.Costs.Select(c => $"{c.Amount * count} {c.Label}"));

    /// <summary>Clicking a placed piece while the guide is on jumps to its step.</summary>
    bool GuideClick()
    {
        if (!_guide || _current != null || _pasting || _hovered == null || !_gIndex.TryGetValue(_hovered, out int i)) return false;
        GuideGo(i);
        return true;
    }
}
