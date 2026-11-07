using System.Collections.Generic;
using Godot;

namespace ValheimPlanner;

/// <summary>
/// Procedural surface look for pieces (no game textures): planks, logs, cut stone, thatch, metal, marble.
/// Patterns are computed in each box's local space so they follow the piece. Per-instance uniforms carry
/// the box size (for edge darkening) and an overlay tint used by the stability / selection views.
/// </summary>
public static class PieceShader
{
    public enum Kind { Planks = 0, Logs = 1, Stone = 2, Metal = 3, Thatch = 4, Marble = 5, Ice = 6, Cloth = 7 }

    const string Code = """
        shader_type spatial;
        render_mode cull_disabled;

        uniform vec3 base_color : source_color = vec3(0.6, 0.45, 0.3);
        uniform int kind = 0;
        instance uniform vec3 box_size = vec3(1.0);
        instance uniform float edge_amt = 1.0;
        instance uniform vec4 tint : source_color = vec4(1.0);
        instance uniform float tint_amt = 0.0;
        instance uniform float emit_amt = 0.0;

        varying vec3 lp;
        varying vec3 ln;

        void vertex() { lp = VERTEX; ln = NORMAL; }

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
        }
        float fbm(vec2 p) { float v = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
        float lum(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
        float seam(float x, float w, float width) {           // dark line every w along x
            float d = min(fract(x / w), 1.0 - fract(x / w)) * w;
            return 1.0 - smoothstep(width * 0.4, width, d);
        }

        void fragment() {
            // 2D coordinates on the current face, long side along uv.x
            vec3 an = abs(ln);
            vec2 uv; vec2 sz;
            if (an.x > an.y && an.x > an.z) { uv = lp.zy; sz = box_size.zy; }
            else if (an.y > an.z)           { uv = lp.xz; sz = box_size.xz; }
            else                            { uv = lp.xy; sz = box_size.xy; }
            if (sz.y > sz.x + 0.01) { uv = uv.yx; sz = sz.yx; }

            vec3 col = base_color;
            float rough = 0.85, metal = 0.0;

            if (kind == 0) {                                   // planks
                float pw = 0.25;
                float row = floor(uv.y / pw);
                col *= 0.80 + 0.35 * hash(vec2(row, 3.7));
                col *= 0.80 + 0.35 * fbm(vec2(uv.x * 1.2 + row * 9.1, uv.y * 30.0));
                if (sz.y > pw * 1.5) col *= 1.0 - 0.5 * seam(uv.y, pw, 0.012);
                col *= 1.0 - 0.35 * seam(uv.x + hash(vec2(row, 1.3)) * 2.0, 2.0, 0.01);
            } else if (kind == 1) {                            // logs / core wood
                col *= 0.80 + 0.35 * fbm(vec2(uv.x * 0.8, uv.y * 22.0));
                col *= 0.9 + 0.2 * sin(uv.y * 40.0 + fbm(uv * 3.0) * 6.0) * 0.5;
                rough = 0.9;
            } else if (kind == 2) {                            // cut stone blocks
                vec2 b = uv / vec2(0.5, 0.25);
                b.x += 0.5 * mod(floor(b.y), 2.0);
                vec2 f = fract(b);
                float d = min(min(f.x, 1.0 - f.x) * 0.5, min(f.y, 1.0 - f.y) * 0.25);
                float mortar = 1.0 - smoothstep(0.008, 0.022, d);
                col *= 0.80 + 0.30 * hash(floor(b));
                col *= 0.80 + 0.35 * fbm(uv * 7.0);
                col = mix(col, col * 0.5, mortar);
                rough = 0.95;
            } else if (kind == 3) {                            // metal
                col *= 0.75 + 0.4 * fbm(uv * 4.0);
                metal = 0.7; rough = 0.45;
            } else if (kind == 4) {                            // thatch: straw strands in overlapping rows
                float rowh = 0.33;
                float row = floor(uv.y / rowh);
                float strands = fbm(vec2(uv.x * 45.0 + row * 13.0, uv.y * 2.0));
                col *= 0.70 + 0.45 * strands;
                col *= 0.85 + 0.25 * hash(vec2(row, 7.7));
                col *= 1.0 - 0.35 * smoothstep(0.6, 1.0, fract(uv.y / rowh));
                rough = 1.0;
            } else if (kind == 5) {                            // marble
                float v = sin(uv.x * 2.5 + fbm(uv * 2.0) * 8.0);
                col *= 0.85 + 0.15 * v;
                rough = 0.35;
            } else if (kind == 6) {                            // ice
                col *= 0.9 + 0.2 * fbm(uv * 3.0);
                rough = 0.15;
            } else {                                           // cloth / leather: fine weave
                float w = 0.5 + 0.5 * sin(uv.x * 220.0) * sin(uv.y * 220.0);
                col *= 0.85 + 0.12 * w + 0.15 * fbm(uv * 5.0);
                rough = 1.0;
            }

            // darkened edges give every block a readable silhouette
            vec2 e = sz * 0.5 - abs(uv);
            float edge = min(e.x, e.y);
            col *= mix(1.0, 0.55 + 0.45 * smoothstep(0.0, 0.05, edge), edge_amt);

            float l0 = max(lum(base_color), 0.01);
            col = mix(col, tint.rgb * clamp(lum(col) / l0, 0.55, 1.35), tint_amt);
            ALBEDO = col;
            ROUGHNESS = rough;
            METALLIC = metal * (1.0 - tint_amt);
            EMISSION = tint.rgb * emit_amt;
        }
        """;

    static Shader? _shader;
    static readonly Dictionary<(Kind, Color), ShaderMaterial> Mats = new();

    public static Kind KindOf(PieceDef d)
    {
        string l = d.EnglishName.ToLowerInvariant();
        if (l.Contains("chaume") || l.Contains("thatch")) return Kind.Thatch;
        return d.Material switch
        {
            "HardWood" => Kind.Logs,
            "Stone" or "Ashstone" => Kind.Stone,
            "Iron" => Kind.Metal,
            "Marble" => Kind.Marble,
            "Ice" => Kind.Ice,
            _ => l.Contains("bûche") || l.Contains("log") ? Kind.Logs : Kind.Planks,
        };
    }

    public static Color BaseColor(PieceDef d, Kind k) => k switch
    {
        Kind.Thatch => new Color(0.78f, 0.64f, 0.36f),
        _ => PieceVisuals.ColorFor(d.Material),
    };

    /// <summary>Colour for a sub-part whose look differs from the piece (metal fittings, cloth…).</summary>
    public static Color DefaultColor(Kind k, PieceDef d) => k switch
    {
        Kind.Planks => KindOf(d) == Kind.Planks ? PieceVisuals.ColorFor(d.Material ?? "Wood") : PieceVisuals.ColorFor("Wood"),
        Kind.Logs => PieceVisuals.ColorFor("HardWood"),
        Kind.Stone => d.Material == "Ashstone" ? PieceVisuals.ColorFor("Ashstone") : PieceVisuals.ColorFor("Stone"),
        Kind.Metal => new Color(0.30f, 0.31f, 0.33f),
        Kind.Thatch => new Color(0.78f, 0.64f, 0.36f),
        Kind.Marble => PieceVisuals.ColorFor("Marble"),
        Kind.Ice => PieceVisuals.ColorFor("Ice"),
        _ => new Color(0.55f, 0.36f, 0.26f),             // cloth / leather
    };

    public static ShaderMaterial MaterialFor(PieceDef d) => MaterialFor(KindOf(d), BaseColor(d, KindOf(d)));

    public static ShaderMaterial MaterialFor(Kind k, Color c)
    {
        if (!Mats.TryGetValue((k, c), out var m))
        {
            _shader ??= new Shader { Code = Code };
            m = new ShaderMaterial { Shader = _shader };
            m.SetShaderParameter("base_color", c);
            m.SetShaderParameter("kind", (int)k);
            Mats[(k, c)] = m;
        }
        return m;
    }

    public static void SetTint(GeometryInstance3D g, Color tint, float amount, float emit = 0f)
    {
        g.SetInstanceShaderParameter("tint", tint);
        g.SetInstanceShaderParameter("tint_amt", amount);
        g.SetInstanceShaderParameter("emit_amt", emit);
    }
}
