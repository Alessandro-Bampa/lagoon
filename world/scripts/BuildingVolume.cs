using Godot;

namespace Lagoon;

/// <summary>
/// Descrizione geometrica di un edificio ai fini del cutaway della camera (skill
/// <c>building-cutaway</c>): dove sta, quanti piani ha, a che quota comincia ciascuno.
///
/// E' un DATO PASSIVO: non decide nulla, non tocca la camera, non ha stato di gioco e non parla con
/// la rete. Chi decide cosa disegnare e' <see cref="BuildingCullController"/>, che vive sull'avatar
/// LOCALE. L'inversione e' deliberata: un manager montato sull'edificio, con un "piano corrente"
/// unico e un riferimento alla camera, sarebbe corretto solo in singleplayer — con quattro giocatori
/// in stanze diverse i quattro peer si sovrascriverebbero a vicenda lo stesso campo. L'edificio non
/// puo' sapere "in che piano si e'", perche' la domanda non ha una sola risposta.
///
/// La geometria e' un parallelepipedo allineato agli assi LOCALI del nodo, quindi un edificio
/// ruotato funziona: i test avvengono in coordinate locali via <c>ToLocal</c>. Le quote dei piani
/// sono bande esplicite e non <c>Area3D</c> trigger: niente ordinamento <c>body_entered</c>/
/// <c>body_exited</c> da sbrogliare sulle scale fra due piani adiacenti, e la stessa domanda si puo'
/// fare per un punto qualunque (debug, NPC) senza che nessuno ci debba camminare dentro.
/// </summary>
public partial class BuildingVolume : Node3D
{
    public const string GroupName = "building";

    /// Margine sotto il pavimento del piano terra entro cui si e' ancora "dentro", in metri.
    private const float GroundTolerance = 0.25f;

    /// Ingombro in pianta (X, Z) in metri, centrato sull'origine del nodo.
    [Export] public Vector2 Footprint { get; set; } = new(10f, 10f);

    /// <summary>
    /// Quota LOCALE del pavimento di ogni piano, in ordine crescente. L'elemento 0 e' il piano
    /// terra. Piu' di <see cref="RenderLayers.MaxFloors"/> voci sono ignorate: oltre non ci sono
    /// render layer per rappresentarle.
    /// </summary>
    [Export] public float[] FloorHeights { get; set; } = new[] { 0f };

    /// <summary>
    /// Quota LOCALE del colmo: sopra si e' fuori dall'edificio. Va messa POCO SOTTO la superficie
    /// calpestabile del tetto, altrimenti chi ci sale sopra continua a contare come "all'ultimo
    /// piano" e si ritrova sotto i piedi il pavimento che il cutaway ha appena tolto.
    /// </summary>
    [Export] public float TopHeight { get; set; } = 3f;

    /// <summary>
    /// Isteresi: quanto piu' larga e' la soglia di USCITA rispetto a quella di ingresso, in metri.
    /// Senza, sulla soglia di una porta il tetto lampeggia a ogni micro-oscillazione della posizione.
    /// </summary>
    [Export] public float ExitHysteresis { get; set; } = 0.5f;

    /// Prefisso dei nodi che raccolgono la struttura di un piano: <c>Floor0</c>, <c>Floor1</c>, ...
    public const string FloorNodePrefix = "Floor";

    /// Numero di piani abitabili.
    public int FloorCount => Mathf.Min(FloorHeights?.Length ?? 0, RenderLayers.MaxFloors);

    /// <summary>
    /// Radici della struttura, risolte per NOME e non esportate: <c>Floor0</c>, <c>Floor1</c>, ...
    /// fino a <c>Floor{FloorCount}</c> — uno in piu' dei piani abitabili.
    ///
    /// Quell'ultimo indice non e' una svista, e' il cuore della convenzione: la copertura del piano
    /// N sta sul layer del piano N+1, perche' il soffitto del piano N E' il pavimento del piano N+1,
    /// lo stesso solaio. Il tetto vero e proprio finisce quindi su un indice che non e' mai "il
    /// piano corrente": da dentro sparisce sempre, da fuori si vede. Senza, stando all'ultimo piano
    /// si guarderebbe il proprio tetto.
    ///
    /// Servono SOLO a spegnere le ombre di cio' che e' nascosto: il <c>CullMask</c> della camera non
    /// tocca la shadow map, quindi un tetto culled continuerebbe a proiettare la sua ombra e la
    /// stanza resterebbe al buio sotto un soffitto invisibile.
    ///
    /// La convenzione e' sui nomi e non su un export perche' l'ordine e' l'unica cosa che conta e
    /// un array di nodi da riempire a mano e' esattamente il posto dove si sbaglia l'ordine.
    /// </summary>
    private readonly System.Collections.Generic.List<Node3D> _floorRoots = new();

    /// Margine sotto il solaio da cui parte il raggio del cursore, in metri.
    private const float CeilingProbeMargin = 0.35f;

    public override void _Ready()
    {
        AddToGroup(GroupName);

        // Un indice oltre i piani abitabili: l'ultimo raccoglie il tetto.
        for (int i = 0; i <= FloorCount; i++)
            _floorRoots.Add(GetNodeOrNull<Node3D>($"{FloorNodePrefix}{i}"));
    }

    /// <summary>
    /// Raccoglie le mesh del piano <paramref name="floorIndex"/> che stanno fra la camera e l'interno
    /// dell'edificio, cioe' i muri "davanti" a chi guarda: quelli che, entrando, coprono la stanza.
    ///
    /// Perche' serve una regola AUTORATA e non una misura: dentro una stanza il giocatore quasi mai
    /// e' coperto dai muri. Con la camera a 40° il raggio verso l'avatar scavalca un muro di 3 m dopo
    /// ~3.6 m, quindi al centro di una stanza cio' che copre e' il SOFFITTO — gia' tolto dal culling
    /// — e i muri restano pieni. Alla lettera e' corretto; da giocare e' inservibile, perche' entrare
    /// in un edificio significa volerne vedere l'interno.
    ///
    /// Quattro filtri, in ordine, e ognuno esiste per un errore concreto:
    /// <list type="number">
    /// <item><b>Il piano si legge dal RENDER LAYER</b>, non dal nodo che contiene la mesh. I muri
    /// rivolti alla camera stanno spesso raggruppati a parte per comodita' di modellazione (in
    /// <c>TestBuilding</c> sotto <c>Shell</c>), e fidarsi della gerarchia li perderebbe tutti in
    /// silenzio. Il layer e' l'unica cosa che l'autore deve indovinare, ed e' gia' quella che comanda
    /// il culling.</item>
    /// <item><b>Solo superfici verticali</b>: se la dimensione minore dell'ingombro e' Y, e' un
    /// solaio o una rampa, non un muro. Sfumare il pavimento su cui si poggiano i piedi aprirebbe un
    /// buco SOTTO al giocatore.</item>
    /// <item><b>Solo muri PERIMETRALI</b>: il centro della mesh deve stare vicino al bordo
    /// dell'<see cref="Footprint"/>, in coordinate locali. E' cio' che separa un muro esterno da un
    /// divisorio interno, e non e' deducibile dal solo lato camera: un tramezzo che per caso sta
    /// nella meta' rivolta all'osservatore passerebbe il test di direzione ed e' esattamente cio' che
    /// non deve sfumare. Un interno visto attraverso i propri tramezzi non si legge piu' come stanze.</item>
    /// <item><b>Solo il lato della camera</b>: il centro della mesh deve stare oltre l'origine
    /// dell'edificio nella direzione di vista, presa sul solo piano ORIZZONTALE. Dei quattro muri
    /// perimetrali restano cosi' i due (o tre) che stanno fra la camera e l'interno.</item>
    /// </list>
    ///
    /// <paramref name="cameraBack"/> e' la direzione "verso la camera", e va ripassata a ogni
    /// interrogazione: da quando la camera RUOTA (Q/E, <see cref="IsometricCamera"/>) l'insieme dei
    /// muri "davanti" cambia mentre si gira, e un valore calcolato una volta sola resterebbe fermo
    /// sull'orientamento iniziale.
    /// </summary>
    public void CollectCameraSideMeshes(
        int floorIndex, Vector3 cameraBack, System.Collections.Generic.List<MeshInstance3D> into)
    {
        uint floorBit = RenderLayers.FloorLayerBit(floorIndex);
        if (floorBit == 0)
            return;

        var flat = new Vector3(cameraBack.X, 0f, cameraBack.Z);
        if (flat.LengthSquared() < 0.0001f)
            return;

        CollectFacing(this, this, flat.Normalized(), floorBit, into);
    }

    private static void CollectFacing(
        Node node, BuildingVolume building, Vector3 flatBack, uint floorBit,
        System.Collections.Generic.List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D mesh && mesh.Mesh != null && (mesh.Layers & floorBit) != 0)
        {
            Aabb aabb = mesh.Mesh.GetAabb();
            Vector3 size = aabb.Size;
            bool vertical = size.Y > size.X || size.Y > size.Z;

            Vector3 center = mesh.GlobalTransform * aabb.GetCenter();

            if (vertical
                && building.IsOnPerimeter(center)
                && (center - building.GlobalPosition).Dot(flatBack) > CameraSideThreshold)
                into.Add(mesh);
        }

        foreach (Node child in node.GetChildren())
            CollectFacing(child, building, flatBack, floorBit, into);
    }

    /// <summary>
    /// Se il punto dato appartiene all'involucro esterno, cioe' sta a ridosso di uno dei quattro lati
    /// dell'<see cref="Footprint"/>. Il test e' in coordinate LOCALI (<c>ToLocal</c>), quindi un
    /// edificio ruotato funziona senza casi particolari, come per <see cref="FloorIndexAt"/>.
    ///
    /// Basta essere vicini a UNO dei due assi: un muro lungo X sta al bordo in Z e in mezzo in X, e
    /// viceversa. Chiedere entrambi selezionerebbe solo i quattro angoli.
    /// </summary>
    public bool IsOnPerimeter(Vector3 worldPoint)
    {
        Vector3 local = ToLocal(worldPoint);

        return Mathf.Abs(local.X) >= Footprint.X * 0.5f - PerimeterMargin
            || Mathf.Abs(local.Z) >= Footprint.Y * 0.5f - PerimeterMargin;
    }

    /// <summary>
    /// Quanto dentro il bordo dell'ingombro puo' stare un muro e contare ancora come perimetrale, in
    /// metri. Copre lo spessore del muro e le imprecisioni di modellazione; oltre il metro comincia a
    /// riprendersi i tramezzi che gli corrono accanto.
    /// </summary>
    private const float PerimeterMargin = 1.0f;

    /// Quanto oltre l'asse dell'edificio deve stare una mesh per contare come "muro davanti", in metri.
    private const float CameraSideThreshold = 1.0f;

    /// <summary>
    /// Indice del piano che contiene <paramref name="worldPos"/> (quota dei PIEDI, non il centro del
    /// corpo), oppure -1 se il punto e' fuori dall'edificio.
    ///
    /// <paramref name="slack"/> allarga sia l'ingombro sia la fascia verticale: chi e' gia' dentro
    /// passa un valore positivo per non uscire al primo millimetro (vedi <see cref="ExitHysteresis"/>).
    /// </summary>
    public int FloorIndexAt(Vector3 worldPos, float slack = 0f)
    {
        int count = FloorCount;
        if (count == 0)
            return -1;

        Vector3 local = ToLocal(worldPos);

        if (Mathf.Abs(local.X) > Footprint.X * 0.5f + slack ||
            Mathf.Abs(local.Z) > Footprint.Y * 0.5f + slack)
            return -1;

        // Sotto le fondamenta o sopra il colmo (sul tetto, in aria): fuori.
        //
        // La tolleranza verso il basso non e' cosmetica: al piano terra i piedi poggiano ESATTAMENTE
        // sulla quota di FloorHeights[0], e senza margine un errore in virgola mobile di un
        // millesimo fa alternare "dentro" e "fuori" a ogni interrogazione — cioe' un tetto che
        // lampeggia stando fermi.
        if (local.Y < FloorHeights[0] - GroundTolerance - slack || local.Y > TopHeight + slack)
            return -1;

        // Il piano corrente e' l'ultimo il cui pavimento sta alla quota dei piedi o sotto. La
        // tolleranza verso il basso copre lo scalino: salendo, i piedi toccano il gradino superiore
        // un istante prima di essere davvero "al piano di sopra".
        int found = 0;
        for (int i = 1; i < count; i++)
        {
            if (local.Y >= FloorHeights[i] - 0.1f)
                found = i;
            else
                break;
        }
        return found;
    }

    /// <summary>
    /// Quota MONDO del soffitto del piano <paramref name="floorIndex"/>: il pavimento del piano
    /// successivo, o il colmo per l'ultimo. E' il piano di taglio del raggio del cursore
    /// (<c>AimResolver.ResolveAimPoint</c>), che sopra quella quota non deve agganciare nulla.
    ///
    /// Si sottrae un margine perche' il raggio deve partire DENTRO la stanza: partendo esattamente
    /// sul solaio si rischia di agganciarne lo spigolo e riportare un punto di mira sul soffitto.
    /// </summary>
    public float CeilingHeightOf(int floorIndex)
    {
        if (floorIndex < 0 || FloorCount == 0)
            return float.PositiveInfinity;

        float localCeiling = floorIndex + 1 < FloorCount ? FloorHeights[floorIndex + 1] : TopHeight;
        return ToGlobal(new Vector3(0f, localCeiling - CeilingProbeMargin, 0f)).Y;
    }

    /// <summary>
    /// Accende o spegne la proiezione d'ombra dei piani, coerentemente con quelli che la camera sta
    /// disegnando. <paramref name="topVisibleFloor"/> negativo (fuori) riaccende tutto.
    ///
    /// E' stato PER PROCESSO, non per camera: le ombre non hanno una maschera di culling. Legittimo
    /// perche' il progetto assume gia' un solo avatar locale per processo — la stessa invariante su
    /// cui poggia il gruppo <c>local_vision</c>.
    ///
    /// Riguarda SOLO i piani culled, ed e' oggi l'UNICO scrittore di <c>CastShadow</c> nel sistema:
    /// le superfici che si aprono restano dentro il <c>CullMask</c> e la loro ombra non viene toccata
    /// da nessuno. Chi aggiungesse un secondo scrittore deve prima decidere in quale dei due mondi
    /// vive — dentro o fuori dal <c>CullMask</c> — perche' i due insiemi devono restare disgiunti.
    /// </summary>
    public void ApplyFloorShadows(int topVisibleFloor)
    {
        for (int i = 0; i < _floorRoots.Count; i++)
            if (_floorRoots[i] is { } root)
                SetShadowsRecursive(root, topVisibleFloor < 0 || i <= topVisibleFloor);
    }

    private static void SetShadowsRecursive(Node node, bool enabled)
    {
        if (node is GeometryInstance3D geometry)
            geometry.CastShadow = enabled
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;

        foreach (Node child in node.GetChildren())
            SetShadowsRecursive(child, enabled);
    }
}
