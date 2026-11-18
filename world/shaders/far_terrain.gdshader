// Terreno lontano della mappa: un solo piano grande quanto la mappa che disegna le terre
// emerse dall'atlante a bassa risoluzione (8 m/px, alfa = terra) generato da osm_terrain.py.
// L'acqua la disegna il piano d'acqua sotto. Dove TerrainStreamer ha caricato il terreno vero
// (loaded_mask = 1) il piano non si disegna: niente doppioni ne' z-fighting.
shader_type spatial;
render_mode cull_back, shadows_disabled, specular_disabled;

uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
// Una texel per region (griglia di MaskRegionMin..MaskRegionMax di TerrainStreamer).
uniform sampler2D loaded_mask : filter_nearest, repeat_disable;
// Rettangolo del mondo coperto da atlante e maschera: (x0, z0, x1, z1).
uniform vec4 world_rect = vec4(-24576.0, -24576.0, 24576.0, 24576.0);

varying vec2 world_xz;

void vertex() {
	world_xz = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
}

void fragment() {
	vec2 uv = (world_xz - world_rect.xy) / (world_rect.zw - world_rect.xy);
	if (texture(loaded_mask, uv).r > 0.5) {
		discard;
	}
	vec4 c = texture(atlas, uv);
	if (c.a < 0.5) {
		discard;
	}
	ALBEDO = c.rgb;
	ROUGHNESS = 1.0;
}
