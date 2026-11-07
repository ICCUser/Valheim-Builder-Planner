# Renders the 1920x480 banner (Nexus Mods header, GitHub social preview) to docs/logo/banner.png.
# Needs a window (not --headless): godot --path app --script ../tools/logo/render_banner.gd
extends SceneTree

const W := 1920
const H := 480
const GOLD := Color(0.86, 0.70, 0.38)
const TEXT := Color(0.92, 0.87, 0.76)

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
	l.add_theme_color_override("font_shadow_color", Color(0, 0, 0, 0.8))
	l.add_theme_constant_override("shadow_offset_x", 3)
	l.add_theme_constant_override("shadow_offset_y", 3)
	return l

func _init() -> void:
	DisplayServer.window_set_size(Vector2i(W, H))
	root.size = Vector2i(W, H)
	var base := ProjectSettings.globalize_path("res://").path_join("..")
	var ui := Control.new()
	ui.size = Vector2(W, H)
	root.add_child(ui)

	var bg := ColorRect.new()
	bg.color = Color(0.047, 0.035, 0.024)
	bg.size = Vector2(W, H)
	ui.add_child(bg)

	# right: a clean 3D view of a real plan (planner --clean capture), fading into the dark background
	var shot := Image.load_from_file(base.path_join("docs/images/banner_source.png"))
	var view := shot.get_region(Rect2i(230, 250, 820, 615))
	view.resize(600, 450, Image.INTERPOLATE_LANCZOS)
	var tex := TextureRect.new()
	tex.texture = ImageTexture.create_from_image(view)
	tex.position = Vector2(W - 600, 15)
	ui.add_child(tex)
	var grad := Gradient.new()
	grad.set_color(0, Color(0.047, 0.035, 0.024, 1))
	grad.set_color(1, Color(0.047, 0.035, 0.024, 0))
	grad.set_offset(1, 0.4)
	var gt := GradientTexture2D.new()
	gt.gradient = grad
	gt.width = 600
	gt.height = 8
	var fade := TextureRect.new()
	fade.texture = gt
	fade.position = Vector2(W - 600, 0)
	fade.size = Vector2(600, H)
	fade.stretch_mode = TextureRect.STRETCH_SCALE
	ui.add_child(fade)
	# left: logo + title
	var logo := TextureRect.new()
	logo.texture = ImageTexture.create_from_image(Image.load_from_file(base.path_join("docs/logo/logo_512.png")))
	logo.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	logo.size = Vector2(340, 340)
	logo.position = Vector2(80, 70)
	ui.add_child(logo)
	ui.add_child(label("VALHEIM BUILDER PLANNER", 66, GOLD, true, Vector2(462, 122)))
	ui.add_child(label("Plan your builds with the real pieces, snapping and stability.", 34, TEXT, false, Vector2(466, 222)))
	ui.add_child(label("Floor plans · materials · step-by-step rebuild guide · 100 % vanilla", 30, TEXT, false, Vector2(466, 272)))
	ui.add_child(label("Free & open source  ·  Unofficial fan tool  ·  36 languages", 24, GOLD.darkened(0.15), false, Vector2(466, 340)))

	# thin gold frame lines
	for y in [10, H - 12]:
		var line := ColorRect.new()
		line.color = GOLD.darkened(0.25)
		line.position = Vector2(20, y)
		line.size = Vector2(W - 40, 2)
		ui.add_child(line)

	for i in 12:
		await process_frame
	var img := root.get_texture().get_image()
	img.save_png(base.path_join("docs/logo/banner.png"))
	print("banner saved %dx%d" % [img.get_width(), img.get_height()])
	quit()
