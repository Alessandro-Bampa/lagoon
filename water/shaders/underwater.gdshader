// Velo sott'acqua: quad a schermo intero, acceso da WaterSurface quando la camera e' sotto la
// superficie. Nebbia dell'acqua in base alla distanza (depth buffer), limitata dalla superficie
// stessa se il raggio sale: oltre quel punto c'e' aria, e la superficie vista da sotto la disegna
// water.gdshader (facce posteriori). Si fonde in alfa: non legge lo screen texture, cosi' resta
// visibile anche l'acqua disegnata nel passaggio trasparente.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, fog_disabled, shadows_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform vec3 scatter_color : source_color = vec3(0.18, 0.32, 0.28);
uniform vec3 absorption = vec3(0.8, 0.4, 0.45);
uniform float turbidity = 0.6;
uniform float water_level = 0.0;

void vertex() {
	// QuadMesh 2x2: copre lo schermo a prescindere dalla camera.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 v = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	vec3 view_pos = v.xyz / v.w;
	float dist = length(view_pos);

	vec3 dir_ws = (INV_VIEW_MATRIX * vec4(normalize(view_pos), 0.0)).xyz;
	vec3 cam_ws = INV_VIEW_MATRIX[3].xyz;
	if (dir_ws.y > 0.0001) {
		dist = min(dist, max(water_level - cam_ws.y, 0.0) / dir_ws.y);
	}

	float density = turbidity + dot(absorption, vec3(0.333));
	float fog = 1.0 - exp(-density * dist);
	// Piu' si scende, piu' e' buio: il colore della torbidita' perde luce con la profondita'.
	float dim = exp(-0.15 * max(water_level - cam_ws.y, 0.0));
	ALBEDO = scatter_color * 0.6 * dim;
	ALPHA = clamp(fog, 0.0, 0.97);
}
