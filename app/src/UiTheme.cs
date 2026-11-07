using Godot;

namespace ValheimPlanner;

/// <summary>
/// Norse look for the whole UI: charcoal/wood panels, thin gold borders, parchment text, serif font.
/// Applied once on the root window so every control, popup and dialog picks it up.
/// </summary>
public static class UiTheme
{
    public static readonly Color Gold = new(0.86f, 0.70f, 0.38f);
    public static readonly Color GoldDim = new(0.50f, 0.40f, 0.23f);
    public static readonly Color Text = new(0.92f, 0.87f, 0.76f);
    public static readonly Color TextDim = new(0.70f, 0.65f, 0.56f);
    static readonly Color Bg = new(0.075f, 0.063f, 0.052f, 0.94f);
    static readonly Color Bg2 = new(0.115f, 0.095f, 0.075f, 0.96f);
    static readonly Color BgSel = new(0.30f, 0.22f, 0.11f, 0.98f);
    static readonly Color BgHover = new(0.17f, 0.135f, 0.095f, 0.98f);

    public static Font SerifFont { get; } = new SystemFont
    {
        // Serif fonts that ship with Windows; other systems fall back to their default serif.
        FontNames = new[] { "Constantia", "Palatino Linotype", "Book Antiqua", "Georgia", "Times New Roman", "serif" },
        Antialiasing = TextServer.FontAntialiasing.Gray,
    };

    public static Font SerifBold { get; } = new SystemFont
    {
        FontNames = new[] { "Constantia", "Palatino Linotype", "Book Antiqua", "Georgia", "Times New Roman", "serif" },
        FontWeight = 700,
    };

    static StyleBoxFlat Box(Color bg, Color border, int borderWidth = 1, int radius = 3, int padX = 8, int padY = 5)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg, BorderColor = border,
            CornerDetail = 4, AntiAliasing = true,
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
        };
        s.SetBorderWidthAll(borderWidth);
        s.SetCornerRadiusAll(radius);
        return s;
    }

    public static Theme Create()
    {
        var t = new Theme { DefaultFont = SerifFont, DefaultFontSize = 15 };

        // panels
        var panel = Box(Bg, GoldDim, 1, 4, 10, 10);
        panel.ShadowColor = new Color(0, 0, 0, 0.45f); panel.ShadowSize = 6;
        t.SetStylebox("panel", "PanelContainer", panel);
        t.SetStylebox("panel", "Panel", panel);
        t.SetColor("font_color", "Label", Text);
        t.SetColor("default_color", "RichTextLabel", Text);

        // buttons (OptionButton, MenuButton inherit from Button)
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal", type, Box(Bg2, new Color(0.33f, 0.26f, 0.16f)));
            t.SetStylebox("hover", type, Box(BgHover, Gold));
            t.SetStylebox("pressed", type, Box(BgSel, Gold));
            t.SetStylebox("hover_pressed", type, Box(BgSel, Gold.Lightened(0.2f)));
            t.SetStylebox("disabled", type, Box(new Color(0.08f, 0.07f, 0.06f, 0.9f), new Color(0.22f, 0.19f, 0.15f)));
            t.SetStylebox("focus", type, new StyleBoxEmpty());
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Gold.Lightened(0.25f));
            t.SetColor("font_pressed_color", type, Gold.Lightened(0.15f));
            t.SetColor("font_hover_pressed_color", type, Gold.Lightened(0.3f));
            t.SetColor("font_focus_color", type, Text);
            t.SetColor("font_disabled_color", type, new Color(0.45f, 0.42f, 0.38f));
        }

        // text fields
        t.SetStylebox("normal", "LineEdit", Box(new Color(0.05f, 0.042f, 0.035f, 0.95f), GoldDim));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0.05f, 0.042f, 0.035f, 0.95f), Gold));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", TextDim with { A = 0.6f });
        t.SetColor("caret_color", "LineEdit", Gold);
        t.SetColor("selection_color", "LineEdit", BgSel);

        // piece grid / lists
        t.SetStylebox("panel", "ItemList", Box(new Color(0.05f, 0.042f, 0.035f, 0.6f), new Color(0.25f, 0.2f, 0.13f), 1, 3, 4, 4));
        t.SetStylebox("selected", "ItemList", Box(BgSel, Gold, 1, 3, 2, 2));
        t.SetStylebox("selected_focus", "ItemList", Box(BgSel, Gold, 1, 3, 2, 2));
        t.SetStylebox("hovered", "ItemList", Box(BgHover, GoldDim, 1, 3, 2, 2));
        t.SetStylebox("cursor", "ItemList", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "ItemList", new StyleBoxEmpty());
        t.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
        t.SetColor("font_color", "ItemList", Text);
        t.SetColor("font_hovered_color", "ItemList", Gold.Lightened(0.2f));
        t.SetColor("font_selected_color", "ItemList", Gold.Lightened(0.25f));
        t.SetConstant("v_separation", "ItemList", 6);
        t.SetConstant("h_separation", "ItemList", 6);

        // menus, tooltips, dialogs
        t.SetStylebox("panel", "PopupMenu", Box(Bg2, Gold, 1, 3, 6, 6));
        t.SetStylebox("hover", "PopupMenu", Box(BgSel, Gold, 0, 2));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Gold.Lightened(0.2f));
        t.SetStylebox("panel", "TooltipPanel", Box(Bg2, Gold, 1, 3, 8, 6));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetStylebox("panel", "AcceptDialog", Box(Bg2, GoldDim, 0, 0, 14, 12));
        t.SetStylebox("embedded_border", "Window", Box(Bg2, Gold, 1, 4, 8, 30));
        t.SetColor("title_color", "Window", Gold);

        // progress + scrollbars
        t.SetStylebox("background", "ProgressBar", Box(new Color(0.05f, 0.042f, 0.035f), GoldDim, 1, 3));
        t.SetStylebox("fill", "ProgressBar", Box(Gold.Darkened(0.25f), Gold, 1, 3));
        t.SetColor("font_color", "ProgressBar", Text);
        foreach (var sb in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", sb, Box(new Color(0, 0, 0, 0.25f), new Color(0, 0, 0, 0), 0, 3, 3, 3));
            t.SetStylebox("grabber", sb, Box(GoldDim, GoldDim, 0, 3));
            t.SetStylebox("grabber_highlight", sb, Box(Gold, Gold, 0, 3));
            t.SetStylebox("grabber_pressed", sb, Box(Gold, Gold, 0, 3));
        }
        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = GoldDim, Thickness = 1 });
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = GoldDim, Thickness = 1, Vertical = true });
        return t;
    }

    /// <summary>Gold small-caps section title with a thin rule under it.</summary>
    public static Control Header(string englishText)
    {
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 2);
        var l = new Label { Text = englishText };
        l.AddThemeColorOverride("font_color", Gold);
        l.AddThemeFontOverride("font", SerifBold);
        l.AddThemeFontSizeOverride("font_size", 13);
        l.Uppercase = true;
        vb.AddChild(l);
        vb.AddChild(new HSeparator());
        return vb;
    }

    public static Button Btn(string englishText, bool toggle = false) => new()
    {
        Text = englishText, ToggleMode = toggle, FocusMode = Control.FocusModeEnum.None,
    };
}
