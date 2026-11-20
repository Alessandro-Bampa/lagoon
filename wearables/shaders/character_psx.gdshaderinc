// Corpo comune dei materiali del personaggio (character_psx.gdshader e character_psx_cloth.gdshader).
//
// Resa come il resto del progetto (RetroMaterials, kit veneziano): texture senza filtro, nessun
// riflesso speculare. In piu' la MASCHERA DELLE REGIONI: ogni vertice porta l'indice della propria
// regione del corpo in COLOR.r (indice / 255, vedi BodyRegion.cs e tools/blender/charlib.py) e
// `hidden_regions` (un bit per regione, per ISTANZA: SetInstanceShaderParameter) scarta i frammenti
// delle regioni coperte da un capo piu' esterno. Lo scarto vale anche per le ombre: una parte
// nascosta non proietta nulla.

uniform sampler2D albedo : source_color, filter_nearest_mipmap, repeat_disable;
uniform vec4 tint : source_color = vec4(1.0);

instance uniform int hidden_regions = 0;

varying flat int region;

void vertex() {
	region = int(round(COLOR.r * 255.0));
}

void fragment() {
	if (((hidden_regions >> region) & 1) != 0) {
		discard;
	}
	vec3 color = texture(albedo, UV).rgb * tint.rgb;
	// l'interno di un capo (maniche, colletto) e' in ombra
	if (!FRONT_FACING) {
		color *= 0.45;
	}
	ALBEDO = color;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
}
