# Renders the banner (Nexus Mods header, recommended 1300x372) to docs/logo/banner.png.
# Needs a window (not --headless): godot --path app --resolution 1300x372 --script ../tools/logo/render_banner.gd
# Background: docs/images/banner_source.png, a capture made with the planner's --clean flag (3D only, no UI).
extends SceneTree

const W := 1300
const H := 372
const GOLD := Color(0.86, 0.70, 0.38)
const TEXT := Color(0.92, 0.87, 0.76)
const DARK := Color(0.047, 0.035, 0.024)

func serif(bold: bool) -> SystemFont:
	var f := SystemFont.new()
	f.font_names = PackedStringArray(["Constantia", "Palatino Linotype", "Book Antiqua", "Georgia", "serif"])
	f.font_weight = 700 if bold else 400
	return f

func label(text: String, size: int, color: Color, bold: bool, pos: Vector2) -> Label:
	var l := Label.new()
	l.text = text
	l.position = pos
	l.add_theme_font_override("font", serif(bold))
	l.add_theme_font_size_override("font_size", size)
	l.add_theme_color_override("font_color", color)
	l.add_theme_color_override("font_shadow_color", Color(0, 0, 0, 0.85))
	l.add_theme_constant_override("shadow_offset_x", 2)
	l.add_theme_constant_override("shadow_offset_y", 2)
	return l

func _init() -> void:
	DisplayServer.window_set_size(Vector2i(W, H))
	root.size = Vector2i(W, H)
	var base := ProjectSettings.globalize_path("res://").path_join("..")
	var ui := Control.new()
	ui.size = Vector2(W, H)
	root.add_child(ui)

	var bg := ColorRect.new()
	bg.color = DARK
	bg.size = Vector2(W, H)
	ui.add_child(bg)

	# the house, close up, blending in behind the text
	var shot := Image.load_from_file(base.path_join("docs/images/banner_source.png"))
	var view := shot.get_region(Rect2i(330, 255, 620, 470))
	view.resize(800, 606, Image.INTERPOLATE_LANCZOS)
	var tex := TextureRect.new()
	tex.texture = ImageTexture.create_from_image(view)
	tex.position = Vector2(W - 800, -110)
	tex.modulate = Color(0.85, 0.85, 0.85)
	ui.add_child(tex)
	var grad := Gradient.new()
	grad.set_color(0, Color(DARK, 1))
	grad.set_color(1, Color(DARK, 0))
	grad.add_point(0.55, Color(DARK, 0.85))
	var gt := GradientTexture2D.new()
	gt.gradient = grad
	gt.width = 800
	gt.height = 8
	var fade := TextureRect.new()
	fade.texture = gt
	fade.position = Vector2(W - 800, 0)
	fade.size = Vector2(800, H)
	fade.stretch_mode = TextureRect.STRETCH_SCALE
	ui.add_child(fade)

	# logo + title
	var logo := TextureRect.new()
	logo.texture = ImageTexture.create_from_image(Image.load_from_file(base.path_join("docs/logo/logo_512.png")))
	logo.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	logo.size = Vector2(264, 264)
	logo.position = Vector2(48, 54)
	ui.add_child(logo)
	ui.add_child(label("VALHEIM BUILDER PLANNER", 54, GOLD, true, Vector2(338, 88)))
	ui.add_child(label("Plan your builds with the real pieces, snapping and stability.", 25, TEXT, false, Vector2(342, 170)))
	ui.add_child(label("Floor plans · materials · step-by-step rebuild guide · 100 % vanilla", 22, TEXT, false, Vector2(342, 207)))
	ui.add_child(label("Free & open source  ·  Unofficial fan tool  ·  36 languages", 18, GOLD.darkened(0.15), false, Vector2(342, 260)))

	# thin gold frame lines
	for y in [8, H - 10]:
		var line := ColorRect.new()
		line.color = GOLD.darkened(0.25)
		line.position = Vector2(14, y)
		line.size = Vector2(W - 28, 2)
		ui.add_child(line)

	for i in 12:
		await process_frame
	var img := root.get_texture().get_image()
	img.save_png(base.path_join("docs/logo/banner.png"))
	print("banner saved %dx%d" % [img.get_width(), img.get_height()])
	quit()
