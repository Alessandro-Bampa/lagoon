// Il campo visivo, come lo vedono gli shader. Incluso da chiunque debba chiedersi «questo punto si
// vede?». Oggi il consumatore e' uno solo — `Shroud.gdshader` — ma il file resta separato perche' la
// maschera non appartiene allo shroud: e' il campo visivo, e lo shroud e' solo il primo a leggerlo.
//
// C'e' stato un secondo lettore, il materiale di mondo, che apriva le superfici quando il personaggio
// vedeva dietro di esse. E' stato rimosso (skill `building-cutaway`). Se tornera', DEVE passare da
// qui e non costruirsi una nozione parallela di visibilita': due risposte diverse alla stessa
// domanda producono superfici che si aprono su terreno che resta al buio, o terreno illuminato
// dietro un muro che resta pieno.
//
// Le globali sono dichiarate in `[shader_globals]` di `project.godot` e devono restarci: Godot le
// risolve quando lo shader COMPILA, prima che qualunque script possa registrarle. Una globale
// mancante fa fallire la compilazione, e un materiale che non compila semplicemente non disegna —
// sintomo muto, salvo il messaggio nei log.

// Maschera polare del ventaglio: RayCount x 1, R32F, r = lunghezza del raggio / vision_range.
//
// repeat_enable fa gestire alla GPU la cucitura a +-PI senza casi particolari, e filter_linear fa
// interpolare fra raggi adiacenti: il bordo resta NETTO IN ANGOLO (le ombre dei muri sono affilate)
// e MORBIDO IN DISTANZA, cioe' la distinzione che una maschera cartesiana sfocata non sa fare.
//
// hint_default_white: se la maschera manca si vede TUTTO. Mai schermo nero al primo frame.
global uniform sampler2D vision_mask : filter_linear, repeat_enable, hint_default_white;

// Posizione XZ dell'occhio in coordinate mondo: il centro della maschera.
global uniform vec2 vision_origin;

// Metri corrispondenti a r = 1.0 nella maschera.
global uniform float vision_range;

// Quota mondo dei piedi dell'osservatore. La usa lo shroud dove non c'e' geometria (il cielo) e
// serve un piano su cui proiettare il raggio della camera.
global uniform float vision_ground;

// Portata GREZZA del ventaglio nella direzione di un punto, bias compreso.
//
// CONVENZIONE ANGOLARE — vale un errore di 90 gradi. Qui si usa atan(z, x), la convenzione
// matematica; il gioco misura l'imbardata come Atan2(dir.X, dir.Z), X per primo. La conversione fra
// le due vive in `VisionSource.ShaderAngleOf` lato C# e non va rifatta qui.
float vision_reach_raw(vec2 world_xz) {
	vec2 d = world_xz - vision_origin;
	float u = (atan(d.y, d.x) + PI) / TAU;
	return texture(vision_mask, vec2(u, 0.5)).r * vision_range;
}

// Quanto un punto del piano orizzontale e' dentro il campo visivo: 1 dentro, 0 fuori, sfumato in
// mezzo.
//
// `bias` e' quanto TOGLIERE alla portata del ventaglio, e per lo shroud vale 0. Il ventaglio supera
// di proposito il punto d'impatto di `VisionSource.SurfaceBias`, perche' la superficie che ti blocca
// la vista TU LA VEDI: senza quel margine il muro cadrebbe in mezzo alla propria sfumatura di bordo,
// cioe' finirebbe in ombra sotto la propria ombra, con gli spigoli che sfarfallano.
//
// Il parametro resta esplicito, anche se oggi lo si chiama solo con 0, perche' chiunque debba
// decidere se si vede ATTRAVERSO una superficie deve passare `SurfaceBias` — e chi se lo dimentica
// tratta come visibile un metro DIETRO ogni muro. E' un errore che il sistema ha gia' fatto.
float vision_visibility(vec2 world_xz, float bias, float softness) {
	float r = vision_reach_raw(world_xz) - bias;
	float dist = distance(world_xz, vision_origin);
	return 1.0 - smoothstep(r - softness, r, dist);
}
