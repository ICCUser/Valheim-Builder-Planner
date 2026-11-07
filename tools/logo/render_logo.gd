# Renders docs/logo/logo.svg to PNGs of several sizes (used for the app icon, README and Nexus).
# Run: godot --headless --path app --script ../tools/logo/render_logo.gd
extends SceneTree

func _init() -> void:
	var root := ProjectSettings.globalize_path("res://").path_join("..")
	var svg := FileAccess.get_file_as_string(root.path_join("docs/logo/logo.svg"))
	var out := root.path_join("docs/logo")
	for size in [16, 24, 32, 48, 64, 128, 256, 512, 1024]:
		var img := Image.new()
		var err := img.load_svg_from_string(svg, size / 512.0)
		if err != OK:
			push_error("render failed for %d: %d" % [size, err])
			continue
		img.save_png(out.path_join("logo_%d.png" % size))
		print("logo_%d.png" % size)
	quit()
