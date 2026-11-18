// Superficie dell'acqua (skill water-ocean). Uniformi scritte ogni frame da WaterSurface.cs.
//
// Vertici: somma di onde di Gerstner IDENTICA a WaveSpectrum.Evaluate (C#), su cui l'host fa
// galleggiare le barche. Se si cambia la formula qui, va cambiata la' (e viceversa).
// Frammenti: normali di dettaglio da due texture che scorrono, profondita' dal depth buffer
// (assorbimento di Beer-Lambert, torbidita', schiuma di riva), rifrazione dallo screen texture,
// riflessi in screen space (SSR) con il cielo dell'ambiente come ripiego, Fresnel di Schlick.
shader_type spatial;
render_mode cull_disabled, depth_draw_always, specular_schlick_ggx, diffuse_lambert;

// ---------------------------------------------------------------- onde (contratto con C#)
uniform int wave_count = 0;
// xy = direzione di propagazione, z = numero d'onda k, w = fase all'origine della griglia (gia'
// comprensiva di k*origine - omega*t, calcolata in double e avvolta in [0, 2pi)).
uniform vec4 wave_a[12];
// x = ampiezza mare aperto, y = spostamento orizzontale mare aperto, z/w = lo stesso, spettro riparato.
uniform vec4 wave_b[12];
uniform float water_level = 0.0;
// Origine mondo (xz) della griglia: la mesh sta li', a quota 0, senza rotazione.
uniform vec2 grid_origin = vec2(0.0);
// grid_origin modulo 1024 m: le texture di dettaglio (periodi che dividono 1024) restano continue.
uniform vec2 tile_origin = vec2(0.0);
// Fuori da questo rettangolo mondo (x0, z0, x1, z1) non c'e' acqua.
uniform vec4 clip_rect = vec4(-1e9, -1e9, 1e9, 1e9);

uniform bool has_exposure_map = false;
uniform sampler2D exposure_map : filter_linear, repeat_disable;
uniform vec4 map_rect = vec4(0.0, 0.0, 1.0, 1.0); // x0, z0, 1/larghezza, 1/profondita'
uniform float default_exposure = 0.5;

// ---------------------------------------------------------------- aspetto (tavolozza della zona)
uniform vec3 scatter_color : source_color = vec3(0.18, 0.32, 0.28);
uniform vec3 absorption = vec3(0.8, 0.4, 0.45);
uniform float turbidity = 0.6;
uniform float foam_palette = 1.0;

// ---------------------------------------------------------------- dettaglio e schiuma
uniform sampler2D detail_a : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D detail_b : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec2 detail_tiling = vec2(16.0, 64.0);   // metri per ripetizione (devono dividere 1024)
uniform vec4 detail_scroll = vec4(0.0);           // offset uv gia' avvolti (a.xy, b.xy), da C#
uniform float detail_strength = 0.4;               // dallo stato del mare
uniform sampler2D foam_tex : filter_linear_mipmap, repeat_enable;
uniform float foam_tiling = 8.0;
uniform vec3 foam_color : source_color = vec3(0.92, 0.95, 0.95);
uniform float shore_foam_depth = 0.08;            // m d'acqua sotto cui compare la schiuma di riva
uniform float shore_foam_amount = 0.55;
uniform float crest_foam_amount = 0.0;            // dallo stato del mare
uniform float crest_fold = 0.75;                   // Jacobiano sotto cui la cresta frange

// ---------------------------------------------------------------- luce
uniform float roughness = 0.04;
uniform float refraction_strength = 0.03;
uniform int ssr_steps = 16;                        // 0 = niente riflessi in screen space
uniform float ssr_max_distance = 200.0;
// La camera e' sotto la superficie (WaterSurface). Le facce posteriori si disegnano "da sotto" solo
// allora: da sopra, il retro di un'onda ripida visto di taglio e' ancora acqua vista da sopra.
uniform bool camera_underwater = false;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

varying vec3 v_normal_ws;
varying float v_fold;
varying float v_height;
varying vec2 v_local;
varying float v_exposure;
varying vec2 v_world_xz;

float exposure_at(vec2 wxz) {
	// Identica a WaterMap.ExposureAt (C#): bilineare, bordi bloccati.
	if (!has_exposure_map) {
		return default_exposure;
	}
	return textureLod(exposure_map, (wxz - map_rect.xy) * map_rect.zw, 0.0).r;
}

void vertex() {
	vec2 p0 = VERTEX.xz;
	float spacing = max(UV2.x, 0.001);
	float expo = exposure_at(grid_origin + p0);
	// Identica a WaveSpectrum.AmplitudesAt: sotto 0,5 lo spettro riparato si spegne, sopra si va al mare.
	float sheltered = clamp(expo * 2.0, 0.0, 1.0);
	float open_sea = clamp(expo * 2.0 - 1.0, 0.0, 1.0);

	vec3 disp = vec3(0.0);
	float dpx_dx = 1.0, dpx_dz = 0.0, dpz_dx = 0.0, dpz_dz = 1.0, dy_dx = 0.0, dy_dz = 0.0;
	for (int i = 0; i < wave_count; i++) {
		vec4 a = wave_a[i];
		vec4 b = wave_b[i];
		float k = a.z;
		// LOD: un'onda piu' corta di 2-4 passi di griglia non si puo' disegnare senza che "nuoti".
		float lod = smoothstep(2.0 * spacing, 4.0 * spacing, 6.2831853 / k);
		if (lod <= 0.0) {
			continue;
		}
		float amp = mix(b.z * sheltered, b.x, open_sea) * lod;
		float hor = mix(b.w * sheltered, b.y, open_sea) * lod;
		vec2 d = a.xy;
		float th = k * dot(d, p0) + a.w;
		float s = sin(th);
		float c = cos(th);
		disp.y += amp * s;
		disp.x += hor * d.x * c;
		disp.z += hor * d.y * c;
		float hk = hor * k * s;
		dpx_dx -= hk * d.x * d.x;
		dpx_dz -= hk * d.x * d.y;
		dpz_dx -= hk * d.x * d.y;
		dpz_dz -= hk * d.y * d.y;
		float ak = amp * k * c;
		dy_dx += ak * d.x;
		dy_dz += ak * d.y;
	}

	vec3 tx = vec3(dpx_dx, dy_dx, dpz_dx);
	vec3 tz = vec3(dpx_dz, dy_dz, dpz_dz);
	v_normal_ws = normalize(cross(tz, tx));
	v_fold = dpx_dx * dpz_dz - dpx_dz * dpz_dx;
	v_local = p0;
	v_exposure = expo;
	VERTEX = vec3(p0.x + disp.x, water_level + disp.y, p0.y + disp.z);
	v_height = VERTEX.y;
	v_world_xz = grid_origin + VERTEX.xz;
	NORMAL = v_normal_ws;
}

vec3 view_from_depth(vec2 uv, float depth, mat4 inv_proj) {
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, depth, 1.0);
	return v.xyz / v.w;
}

// Riflesso in screen space: marcia lungo il raggio riflesso nel depth buffer. Rende solo cio' che e'
// gia' a schermo (palazzi, barche, rive); il resto lo da' il cielo dell'ambiente (SPECULAR di Godot).
vec4 screen_space_reflection(vec3 pos, vec3 n, mat4 proj, mat4 inv_proj, mat4 inv_view) {
	vec3 r = normalize(reflect(normalize(pos), n));
	float up = (inv_view * vec4(r, 0.0)).y;
	if (ssr_steps <= 0 || up <= 0.0) {
		return vec4(0.0);
	}
	float prev_t = 0.0;
	for (int i = 1; i <= ssr_steps; i++) {
		float f = float(i) / float(ssr_steps);
		float t = ssr_max_distance * f * f;          // passi piu' fitti vicino alla superficie
		vec3 p = pos + r * t;
		vec4 clip = proj * vec4(p, 1.0);
		if (clip.w <= 0.0) {
			break;
		}
		vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
		if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
			break;
		}
		vec3 scene = view_from_depth(uv, textureLod(depth_tex, uv, 0.0).r, inv_proj);
		float behind = scene.z - p.z;                // > 0: il raggio e' dietro la scena
		// Il fondale e' sotto l'acqua: nel depth buffer c'e', ma un riflesso non puo' raggiungerlo
		// (senza questo, sulle creste viste di taglio compaiono righe scure: il fondo riflesso).
		bool underwater_hit = (inv_view * vec4(scene, 1.0)).y < water_level - 0.3;
		if (behind > 0.0 && behind < 1.0 + t * 0.15 && !underwater_hit) {
			// Affinamento binario fra il passo precedente e questo.
			float lo = prev_t;
			float hi = t;
			for (int j = 0; j < 4; j++) {
				float mid = 0.5 * (lo + hi);
				vec3 pm = pos + r * mid;
				vec4 cm = proj * vec4(pm, 1.0);
				vec2 um = cm.xy / cm.w * 0.5 + 0.5;
				vec3 sm = view_from_depth(um, textureLod(depth_tex, um, 0.0).r, inv_proj);
				if (sm.z - pm.z > 0.0) {
					hi = mid;
					uv = um;
				} else {
					lo = mid;
				}
			}
			vec2 edge = smoothstep(vec2(0.0), vec2(0.08), uv) * (1.0 - smoothstep(vec2(0.92), vec2(1.0), uv));
			float fade = edge.x * edge.y * (1.0 - f * f) * smoothstep(0.0, 0.15, up);
			return vec4(textureLod(screen_tex, uv, 0.0).rgb, fade);
		}
		prev_t = t;
	}
	return vec4(0.0);
}

void fragment() {
	if (v_world_xz.x < clip_rect.x || v_world_xz.y < clip_rect.y
			|| v_world_xz.x > clip_rect.z || v_world_xz.y > clip_rect.w) {
		discard;
	}

	float dist = length(VERTEX);

	// ---- normale: Gerstner (per vertice) + due texture di dettaglio che scorrono in versi diversi
	vec2 tuv = v_local + tile_origin;
	vec3 na = texture(detail_a, tuv / detail_tiling.x + detail_scroll.xy).rgb * 2.0 - 1.0;
	vec3 nb = texture(detail_b, tuv / detail_tiling.y + detail_scroll.zw).rgb * 2.0 - 1.0;
	float detail = detail_strength * mix(0.35, 1.0, v_exposure) / (1.0 + dist / 400.0);
	vec3 n = normalize(normalize(v_normal_ws) + vec3(na.x + nb.x, 0.0, na.y + nb.y) * detail);
	bool from_below = !FRONT_FACING && camera_underwater;
	if (from_below) {
		n = -n;
	}
	vec3 nv = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
	// Di taglio, una normale (di cresta o di dettaglio) puo' voltare le spalle alla camera: il Fresnel
	// andrebbe a 1 su una faccia che non riflette nulla, cioe' una riga nera. La si piega verso chi guarda.
	float nd = dot(nv, VIEW);
	if (nd < 0.05) {
		nv = normalize(nv + VIEW * (0.05 - nd));
	}
	NORMAL = nv;

	float ndv = clamp(dot(nv, VIEW), 0.0, 1.0);

	if (from_below) {
		// Da sott'acqua: si vede fuori solo dentro la finestra di Snell (~48° dalla verticale), oltre
		// c'e' la riflessione totale interna, cioe' il colore dell'acqua stessa.
		vec3 above = textureLod(screen_tex, SCREEN_UV + nv.xy * 0.05, 0.0).rgb;
		float window = smoothstep(0.62, 0.75, ndv);
		ALBEDO = vec3(0.0);
		EMISSION = mix(scatter_color * 0.25, above * 0.9, window);
		SPECULAR = 0.0;
		ROUGHNESS = 1.0;
	} else {
		// ---- profondita' della scena dietro la superficie
		// Profondita' 0 = piano lontano (Z invertita): dietro non c'e' niente, l'acqua e' "infinita".
		// Senza questo, verso il far della camera lo spessore tende a zero e il mare diventa trasparente.
		float raw_flat = texture(depth_tex, SCREEN_UV).r;
		vec3 bg_flat = view_from_depth(SCREEN_UV, raw_flat, INV_PROJECTION_MATRIX);
		float bg_world_y = (INV_VIEW_MATRIX * vec4(bg_flat, 1.0)).y;
		float vdepth = raw_flat <= 1e-7 ? 1e4 : max(v_height - bg_world_y, 0.0);   // verticale: schiuma

		// ---- rifrazione: spostamento dalla normale, nullo dove l'acqua e' sottile o lontana
		vec2 offs = n.xz * refraction_strength * clamp(vdepth, 0.0, 1.0) / (1.0 + dist * 0.02);
		vec2 ruv = clamp(SCREEN_UV + offs, vec2(0.001), vec2(0.999));
		float raw = texture(depth_tex, ruv).r;
		vec3 bg = view_from_depth(ruv, raw, INV_PROJECTION_MATRIX);
		if (bg.z > VERTEX.z) {
			// Il punto rifratto e' DAVANTI all'acqua (una barca, una gamba): niente rifrazione.
			ruv = SCREEN_UV;
			raw = raw_flat;
			bg = bg_flat;
		}
		float thick = raw <= 1e-7 ? 1e4 : max(length(bg) - dist, 0.0);   // tratto di raggio nell'acqua

		// ---- corpo dell'acqua: fondale attenuato (Beer-Lambert) + colore diffuso della torbidita'
		vec3 behind = textureLod(screen_tex, ruv, 0.0).rgb;
		vec3 transmit = exp(-absorption * thick);
		float scatter = 1.0 - exp(-turbidity * thick);
		vec3 body_emit = behind * transmit * (1.0 - scatter);
		vec3 body_albedo = scatter_color * scatter;

		// ---- schiuma: riva (acqua bassissima, anche attorno agli scafi) e creste che si ripiegano
		float pattern = texture(foam_tex, tuv / foam_tiling + detail_scroll.xy * 0.5).r;
		float shore = (1.0 - smoothstep(0.0, shore_foam_depth, vdepth)) * shore_foam_amount;
		float crest = (1.0 - smoothstep(crest_fold - 0.25, crest_fold + 0.1, v_fold)) * crest_foam_amount;
		float amount = clamp((shore + crest) * foam_palette, 0.0, 1.0);
		// Morbida e semitrasparente: la soglia sul motivo da' la forma, l'ammontare anche l'opacita'.
		float foam = amount > 0.001 ? smoothstep(1.0 - amount, 1.0 - amount + 0.35, pattern) * sqrt(amount) : 0.0;
		foam *= 1.0 - 0.7 * smoothstep(150.0, 600.0, dist);

		// ---- riflessi
		float fresnel = 0.02 + 0.98 * pow(1.0 - ndv, 5.0);
		vec4 ssr = screen_space_reflection(VERTEX, nv, PROJECTION_MATRIX, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);

		// Il cielo e il sole li aggiunge Godot (SPECULAR, Fresnel incluso); dove l'SSR ha trovato
		// qualcosa si spegne lo speculare dell'ambiente per non riflettere due volte.
		ALBEDO = mix(body_albedo * (1.0 - fresnel), foam_color, foam);
		EMISSION = (body_emit * (1.0 - fresnel) + ssr.rgb * fresnel * ssr.a) * (1.0 - foam);
		SPECULAR = 0.5 * (1.0 - ssr.a) * (1.0 - foam);
		ROUGHNESS = mix(roughness, 0.85, foam);
		METALLIC = 0.0;
	}
}
