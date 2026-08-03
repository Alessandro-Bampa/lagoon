// Superficie standard della geometria di mondo. Rimpiazza StandardMaterial3D ovunque serva che una
// mesh possa SFUMARE quando sta fra la camera e il giocatore (skill `building-cutaway`).
//
// Qui c'e' solo la RESA. CHI sfuma lo decide `BuildingCullController`, sul giocatore locale, e oggi
// la regola e' una sola e autorata: entrando in un edificio sfumano i muri del piano corrente
// rivolti alla camera.
//
// ============================================================================================
//  NOTA STORICA: qui c'e' stato un reveal PER FRAMMENTO pilotato dal campo visivo
// ============================================================================================
//
// L'idea era: «se il personaggio puo' vedere cosa c'e' dietro un ostacolo, l'ostacolo si apre».
// Ogni frammento campionava la maschera polare del ventaglio lungo la colonna che nasconde alla
// camera, con un gate di piano e un blob attorno all'avatar. E' stato RIMOSSO: sulla carta e'
// coerente, in gioco produceva piu' problemi di quanti ne risolvesse — mezzo mondo che si apre
// mentre ci si muove, e nessun modo di prevedere cosa sarebbe diventato trasparente.
//
// Se un giorno lo si riprende, due cose vanno sapute prima di ricominciare:
//   * la sottrazione di `SurfaceBias` dal ventaglio non e' opzionale, o ogni muro apre se' stesso
//     (skill `vision-fog` §5);
//   * il contorno tramite guscio invertito (`next_pass` + `cull_front`) NON funziona con un
//     materiale che si buca col discard: riempie il buco invece di orlarlo, perche' le back-face
//     cresciute restano visibili solo finche' la mesh originale le copre.
//
// ============================================================================================
//  QUESTO MATERIALE E' OPACO, E DEVE RESTARLO. Non scrivere mai ALPHA.
// ============================================================================================
//
// La granularita' si ottiene con `discard` su una soglia ordinata (Bayer 4x4), non mescolando in
// alpha. E' la tecnica standard del genere — «dithered/masked transparency» — e la ragione e' che un
// materiale trasparente in Godot esce dalla coda opaca, e con lui saltano tre cose in una volta:
//
//   1. l'ORDINAMENTO diventa per distanza del centro dell'AABB. Il pavimento 40x40 ha il centro
//      all'origine e finisce disegnato dopo un muro vicino alla camera. Il sintomo non e' "un muro
//      si vede male": e' il pavimento che si buca e sotto compare lo skybox.
//   2. il DEPTH BUFFER non e' piu' affidabile, e lo Shroud (skill `vision-fog`) ci ricostruisce
//      sopra la posizione mondo: il cono di visione si rompe proprio sulle superfici sfumate.
//   3. tutto cio' che si affidava a essere disegnato DOPO il mondo perde la garanzia — le etichette
//      degli oggetti a terra (`Label3D` con `no_depth_test`) finiscono dietro ai muri.
//
// Nessuno dei tre si ripara con un render mode: si riparano restando opachi. Basta non toccare
// ALPHA e lasciare `depth_draw_opaque` (il default), che qui e' corretto proprio perche' il
// materiale E' opaco.
//
// Il prezzo del `discard` e' che i frammenti scartati non scrivono profondita', quindi lo Shroud
// campiona la geometria dietro il buco. E' accettato: dietro un muro c'e' la stanza, a 30 cm, e la
// nebbia varia su scala di metri — la differenza e' invisibile su una superficie che e' gia', di
// proposito, a puntini.
//
// A fade = 0 non si scarta nulla e non si paga niente: il mondo a riposo e' geometria opaca normale,
// e sparisce anche il flickering fra facce complanari, che veniva dall'ordinamento per oggetto.
shader_type spatial;
render_mode cull_back, diffuse_burley;

uniform vec3 albedo : source_color = vec3(0.62, 0.60, 0.56);
uniform float roughness : hint_range(0.0, 1.0) = 0.9;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

// Frazione di pixel scartati a sfumatura piena. Il compromesso e' fra due estremi entrambi sbagliati:
// sotto ~0.5 la superficie resta troppo leggibile e non si vede cosa c'e' dietro; salendo verso 1.0
// sparisce, e con lei il contesto spaziale — il motivo per cui si usa il dither invece di nascondere
// la mesh. La parte NON scartata e' quella che tiene l'accenno della forma, e serve che ci sia.
//
// Provato a 0.88 per rendere le superfici «piu' trasparenti»: la grana si sfalda e il risultato e'
// peggiore, non piu' leggibile. Il valore giusto e' piu' basso di quanto suggerisca l'istinto.
uniform float max_discard : hint_range(0.0, 1.0) = 0.72;

// Lato in pixel di una cella del pattern. Ancorato allo SCHERMO e non alla UV: il reticolo deve
// restare della stessa grana comunque siano orientate e scalate le mesh.
uniform float cell_pixels : hint_range(1.0, 16.0) = 3.0;

// 0 = superficie piena, 1 = superficie completamente sfumata. Parametro d'ISTANZA: un solo materiale
// condiviso da tutte le mesh che lo usano, un valore per mesh, nessuna duplicazione di risorse.
//
// ATTENZIONE: SetInstanceShaderParameter su un materiale che non dichiara questo parametro NON e' un
// errore, il valore si perde e basta. E' il modo muto in cui questo sistema si rompe.
instance uniform float fade : hint_range(0.0, 1.0) = 0.0;

// Matrice di Bayer 4x4, la soglia ordinata classica. Restituisce 0..15 / 16.
// Ordinata e non a rumore: il pattern deve restare fermo mentre ci si muove, o "formicola".
float bayer4(vec2 cell) {
	int x = int(mod(cell.x, 4.0));
	int y = int(mod(cell.y, 4.0));
	int index = x + y * 4;

	// Scritta come catena di confronti e non come array: un array costante indicizzato a runtime
	// costa uno spill su alcuni backend, e qui siamo per-frammento su superfici grandi.
	float v = 0.0;
	v = (index == 0) ? 0.0 : v;    v = (index == 1) ? 8.0 : v;
	v = (index == 2) ? 2.0 : v;    v = (index == 3) ? 10.0 : v;
	v = (index == 4) ? 12.0 : v;   v = (index == 5) ? 4.0 : v;
	v = (index == 6) ? 14.0 : v;   v = (index == 7) ? 6.0 : v;
	v = (index == 8) ? 3.0 : v;    v = (index == 9) ? 11.0 : v;
	v = (index == 10) ? 1.0 : v;   v = (index == 11) ? 9.0 : v;
	v = (index == 12) ? 15.0 : v;  v = (index == 13) ? 7.0 : v;
	v = (index == 14) ? 5.0 : v;   v = (index == 15) ? 13.0 : v;
	return v / 16.0;
}

void fragment() {
	if (fade > 0.002) {
		float threshold = bayer4(floor(FRAGCOORD.xy / max(cell_pixels, 1.0)));
		if (threshold < clamp(fade, 0.0, 1.0) * max_discard) {
			discard;
		}
	}

	ALBEDO = albedo;
	ROUGHNESS = roughness;
	METALLIC = metallic;
}
