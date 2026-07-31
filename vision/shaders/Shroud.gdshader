// Shroud: scurisce cio' che sta fuori dal campo visivo dell'avatar locale.
//
// Post-process fullscreen su un quad figlio della camera. Ricostruisce la posizione MONDO di ogni
// pixel dal depth buffer e campiona una maschera POLARE (una riga di raggi attorno al giocatore).
//
// Perche' in spazio mondo e non in spazio schermo: IsometricCamera.AddKick() trasla la camera a
// ogni sparo. Una maschera in spazio schermo scivolerebbe a ogni colpo; una in spazio mondo e'
// immune per costruzione.
//
// Perche' polare: i dati di partenza SONO polari (un raggio per settore angolare). Rasterizzarli in
// una texture cartesiana e ricampionarli perderebbe informazione due volte, e il filtro bilineare
// su texel grossi farebbe colare la luce oltre gli spigoli dei muri — cioe' proprio l'informazione
// tattica che lo shroud deve negare.

shader_type spatial;

// blend_mul mette il materiale nella passata TRASPARENTE (condizione necessaria per poter leggere
// DEPTH_TEXTURE) e moltiplica il colore gia' presente: niente copia del backbuffer, niente
// hint_screen_texture.
render_mode unshaded, blend_mul, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled;

// Maschera polare, larghezza = RayCount, altezza 1, formato R32F.
// r = lunghezza del raggio normalizzata su radius_scale.
// repeat_enable fa gestire alla GPU la cucitura a +-PI senza casi particolari.
// hint_default_white: se la texture manca si vede TUTTO. Mai schermo nero al primo frame.
uniform sampler2D radii_tex : filter_linear, repeat_enable, hint_default_white;

// Profondita' della scena. In Godot 4.7 il builtin DEPTH_TEXTURE NON esiste piu': va dichiarato
// come uniform con hint_depth_texture. Usarlo come builtin fa fallire la compilazione, e un
// materiale che non compila semplicemente non disegna nulla — sintomo che si scambia per "lo
// shroud non funziona" invece che per un errore di shader.
// filter_nearest e non lineare: interpolare valori di profondita' attraverso i bordi della
// geometria produce posizioni mondo che non esistono, e quindi aloni sui contorni.
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;

// Posizione XZ dell'occhio in coordinate mondo.
uniform vec2 origin_xz = vec2(0.0);

// Metri corrispondenti a r = 1.0 nella texture.
uniform float radius_scale = 30.0;

// Sfumatura del bordo, in METRI (non in texel: e' il vantaggio della forma polare).
uniform float edge_softness = 0.9;

// Quanto si scurisce fuori dal campo visivo. Va tarato A OCCHIO e non calcolato: il quad
// moltiplica in HDR lineare PRIMA del tonemap Filmic, che ricomprime, quindi un x0.7 lineare non
// si legge come "30% piu' scuro" a schermo.
uniform float darkness : hint_range(0.0, 1.0) = 0.55;

// Tinta della zona in ombra. NIENTE source_color: e' un moltiplicatore lineare, non un colore sRGB.
uniform vec3 shroud_tint = vec3(0.80, 0.88, 1.0);

// Quota del piano su cui ripiegare dove non c'e' geometria (cielo).
uniform float ground_y = 0.0;

uniform bool enabled = true;

// Diagnostica: 0 = normale, 1 = scacchiera in coordinate mondo, 2 = depth grezzo.
// La scacchiera deve risultare ANCORATA AL MONDO e salire su molo, rampa e barca.
uniform int debug_mode : hint_range(0, 2) = 0;

vec3 world_from_ndc(vec2 uv, float d, mat4 inv_proj, mat4 inv_view) {
	// NOTA: si usa d GREZZO. Qualunque frammento con "d * 2.0 - 1.0" viene da OpenGL/Godot 3 e da'
	// una ricostruzione plausibile ma sbagliata, con errore crescente con la distanza.
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, d, 1.0);
	v.xyz /= v.w;   // con la proiezione ortogonale w resta 1: no-op innocuo, ma tienilo.
	return (inv_view * vec4(v.xyz, 1.0)).xyz;
}

void vertex() {
	// Quad fullscreen: si bypassa la MVP e si scrive direttamente il clip space.
	// QuadMesh.Size = (1,1) -> VERTEX.xy in [-0.5, 0.5], quindi il fattore 2.
	POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
}

void fragment() {
	float depth = texture(depth_texture, SCREEN_UV).r;

	vec3 world;
	if (depth <= 0.0) {
		// Nessuna geometria: cielo. Si proietta il raggio della camera sul piano ground_y, cosi'
		// lo shroud prosegue senza cucitura oltre il bordo del pavimento. Senza questo ramo la
		// ricostruzione restituirebbe un punto sul far plane con XZ arbitrario, e il cielo
		// lampeggerebbe fra chiaro e scuro.
		vec3 p0 = world_from_ndc(SCREEN_UV, 1.0, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
		vec3 p1 = world_from_ndc(SCREEN_UV, 0.5, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
		vec3 rd = normalize(p1 - p0);
		float den = (abs(rd.y) < 1e-4) ? 1e-4 : rd.y;
		world = p0 + rd * max((ground_y - p0.y) / den, 0.0);
	} else {
		world = world_from_ndc(SCREEN_UV, depth, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
	}

	vec2 d = world.xz - origin_xz;
	float dist = length(d);

	// d.y e' world.z: convenzione atan2(z, x), che NON e' quella del gioco (Atan2(x, z)).
	// La conversione sta in VisionSource.ShaderAngleOf, lato C#.
	float u = (atan(d.y, d.x) + PI) / TAU;
	float r = texture(radii_tex, vec2(u, 0.5)).r * radius_scale;

	// Netto in angolo (ombre dei muri affilate), morbido in distanza (limite del raggio sfumato).
	float vis = 1.0 - smoothstep(r - edge_softness, r, dist);
	vis = enabled ? vis : 1.0;

	vec3 shaded = mix(shroud_tint * (1.0 - darkness), vec3(1.0), vis);

	// Diagnostica, senza return anticipati: il linguaggio di shading di Godot non li ammette
	// dentro fragment(), e usarli fa fallire la compilazione in SILENZIO (il quad semplicemente
	// non viene disegnato, che si scambia per "lo shroud non funziona").
	if (debug_mode == 1) {
		shaded = vec3(fract(world.xz * 0.2), 0.0);
	} else if (debug_mode == 2) {
		shaded = vec3(depth);
	}

	ALBEDO = shaded;
	ALPHA = 1.0;
}
