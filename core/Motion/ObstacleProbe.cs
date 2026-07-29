using Godot;

namespace Lagoon;

/// <summary>
/// Misura la forma dell'ostacolo che il personaggio ha davanti.
///
/// E' l'alternativa al taggare a mano ogni muretto del livello con "scavalcabile": in un open world
/// quella marcatura e' lavoro di level design continuo e si dimentica sempre da qualche parte. Qui
/// la geometria si MISURA a runtime — altezza, spessore, orientamento della parete, spazio
/// d'atterraggio — cosi' il sistema funziona su qualunque mesh, comprese le forme irregolari.
///
/// Non e' un nodo e non tocca niente: come <see cref="WeaponSpaceProbe"/> misura e basta. Chi
/// decide cosa farne (scavalcare, arrampicarsi, saltare e basta) e' <see cref="CharacterMotor"/>.
/// Sta in <c>core/</c> e non dipende da <c>player/</c>: la stessa sonda serve al giocatore e agli
/// NPC, che ereditano dallo stesso motore di movimento.
///
/// Non ha stato fra una chiamata e l'altra: e' statica apposta, cosi' non c'e' modo di leggere per
/// sbaglio la misura di un frame precedente.
/// </summary>
public static class ObstacleProbe
{
    /// Maschera degli strati misurati: il mondo statico e il ponte dei veicoli (ci si scavalca sopra).
    private const uint ProbeMask = CollisionLayers.World | CollisionLayers.VehicleDeck;

    /// Componente verticale della normale oltre la quale non e' un muro ma una rampa o un pavimento.
    private const float MaxWallNormalY = 0.4f;

    /// Passo di campionamento dello spessore, in metri.
    private const float DepthStep = 0.1f;

    /// Scostamento laterale dei due raggi del corner check, in metri.
    private const float CornerOffset = 0.25f;

    /// Divergenza massima fra la normale centrale e quelle laterali perche' la parete sia "piana".
    private const float CornerNormalTolerance = 0.86f; // cos(30 gradi)

    /// <summary>
    /// Esito di una misura. Sono tutte grandezze GEOMETRICHE in coordinate mondo: nessuna
    /// classificazione di gioco, nessuna soglia applicata. Le soglie le mette chi ha chiamato.
    /// </summary>
    public readonly struct ObstacleInfo
    {
        /// Falso se una qualunque delle misure e' mancata: in quel caso gli altri campi non valgono.
        public bool Found { get; init; }

        /// Altezza del bordo superiore rispetto ai PIEDI di chi ha misurato, in metri.
        public float Height { get; init; }

        /// <summary>
        /// Spessore dell'ostacolo lungo la direzione di marcia, in metri. E' il dato che permette di
        /// atterrare subito dietro un muretto sottile e piu' in la' dietro uno spesso, invece di
        /// usare una distanza fissa che sbaglia in entrambi i casi.
        /// </summary>
        public float Depth { get; init; }

        /// Punto colpito sulla faccia verticale della parete.
        public Vector3 WallPoint { get; init; }

        /// Punto del bordo superiore, sulla verticale della parete: e' l'appiglio delle mani.
        public Vector3 LedgePoint { get; init; }

        /// Bordo opposto della sommita', dove l'ostacolo finisce.
        public Vector3 FarEdgePoint { get; init; }

        /// Suolo oltre l'ostacolo: dove si finisce scavalcando.
        public Vector3 LandingPoint { get; init; }

        /// <summary>
        /// Normale della parete, orizzontale e normalizzata: punta VERSO chi ha misurato. Serve ad
        /// allineare il personaggio al muro (muri angolati) e a orientare le mani sul bordo.
        /// </summary>
        public Vector3 WallNormal { get; init; }

        /// Falso se nel punto d'atterraggio c'e' altra geometria: scavalcare li' incastrerebbe.
        public bool LandingClear { get; init; }

        /// Falso se la sommita' e' troppo stretta per starci in piedi (rilevante per il mantle).
        public bool TopStandable { get; init; }
    }

    /// <summary>
    /// Misura l'ostacolo davanti a <paramref name="feet"/> nella direzione <paramref name="forward"/>.
    ///
    /// Cinque misure in cascata, ognuna delle quali puo' far fallire l'insieme:
    /// (1) c'e' una parete verticale entro <paramref name="reach"/>;
    /// (2) e' una parete VERA e non uno spigolo preso di striscio (corner check a tre raggi);
    /// (3) la sua sommita' e' raggiungibile entro <paramref name="maxHeight"/>;
    /// (4) quanto e' spessa;
    /// (5) c'e' suolo libero dove atterrare.
    ///
    /// Costa una quindicina di raycast, quindi si chiama SOLO quando serve davvero (alla richiesta
    /// di salto), mai a ogni frame.
    /// </summary>
    /// <param name="world">Mondo fisico da interrogare.</param>
    /// <param name="selfRid">Corpo di chi misura, escluso dai raggi.</param>
    /// <param name="feet">Punto a terra fra i piedi, in coordinate mondo.</param>
    /// <param name="forward">Direzione di marcia, orizzontale e normalizzata.</param>
    /// <param name="maxHeight">Altezza oltre la quale non si cerca nemmeno il bordo, in metri.</param>
    /// <param name="probeHeight">
    /// Quota a cui si cerca la faccia della parete, in metri. Va tenuta SOTTO il piu' basso degli
    /// ostacoli che interessano: sondare a mezza altezza della banda sembra naturale e invece manca
    /// del tutto i muretti bassi — il raggio passa sopra il muro e non trova niente.
    /// </param>
    /// <param name="reach">Distanza massima dalla parete perche' conti, in metri.</param>
    /// <param name="maxDepth">Spessore oltre il quale si smette di misurare, in metri.</param>
    /// <param name="landingMargin">
    /// Quanto oltre il bordo opposto si cerca il suolo, in metri. E' un parametro e non una
    /// costante perche' <see cref="ObstacleInfo.LandingPoint"/> deve essere il punto in cui si
    /// atterra DAVVERO: verificare l'ingombro a ridosso dello spigolo darebbe sempre "occupato",
    /// visto che li' la capsula tocca ancora la parete appena scavalcata.
    /// </param>
    /// <param name="landingShape">Capsula di chi misura, per verificare l'ingombro d'atterraggio.</param>
    public static ObstacleInfo Scan(World3D world, Rid selfRid, Vector3 feet, Vector3 forward,
        float maxHeight, float probeHeight, float reach, float maxDepth, float landingMargin,
        CapsuleShape3D? landingShape)
    {
        PhysicsDirectSpaceState3D space = world.DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid> { selfRid };

        // (1) La faccia della parete, sondata in basso: sopra ci si perderebbero i muretti, sotto
        // si prenderebbe qualunque gradino che il motore sale gia' da solo.
        Vector3 chest = feet + Vector3.Up * probeHeight;
        if (!Cast(space, exclude, chest, chest + forward * reach, out Vector3 wallPoint, out Vector3 wallNormal))
            return default;

        if (Mathf.Abs(wallNormal.Y) > MaxWallNormalY)
            return default; // rampa o pavimento: si sale camminandoci sopra, non scavalcando.

        wallNormal = new Vector3(wallNormal.X, 0f, wallNormal.Z).Normalized();
        if (wallNormal.LengthSquared() < 0.5f)
            return default;

        // (2) Corner check: due raggi paralleli ai lati. Senza, basta che il raggio centrale
        // prenda lo spigolo di un pilastro per agganciare uno scavalcamento nel vuoto.
        Vector3 tangent = wallNormal.Cross(Vector3.Up).Normalized();
        if (!ConfirmFlatWall(space, exclude, chest, forward, reach, wallNormal, tangent))
            return default;

        // (3) La sommita': raggio verso il basso da sopra il bordo, appena OLTRE la parete. Misura
        // il bordo VERO, quindi funziona anche su muri smussati o con detriti in cima.
        Vector3 above = wallPoint - wallNormal * 0.08f;
        above.Y = feet.Y + maxHeight + 0.3f;
        if (!Cast(space, exclude, above, above + Vector3.Down * (maxHeight + 0.4f),
                out Vector3 ledgeTop, out _))
            return default; // niente bordo entro l'altezza cercata: e' un muro, non un ostacolo.

        float height = ledgeTop.Y - feet.Y;
        var ledge = new Vector3(wallPoint.X, ledgeTop.Y, wallPoint.Z);

        // (4) Lo spessore: si avanza a piccoli passi sulla sommita' finche' non manca il terreno
        // sotto. E' cio' che distingue un muretto da un parapetto largo un metro.
        (float depth, Vector3 farEdge) = MeasureDepth(space, exclude, ledge, -wallNormal, maxDepth);

        // (5) L'atterraggio, oltre il bordo lontano. Senza suolo di la' non si scavalca alla cieca.
        Vector3 beyond = farEdge - wallNormal * landingMargin;
        beyond.Y = ledgeTop.Y + 0.3f;
        Vector3 landing = farEdge;
        bool landingFound = Cast(space, exclude, beyond,
            beyond + Vector3.Down * (height + 1.5f), out Vector3 groundPoint, out _);
        if (landingFound)
            landing = groundPoint;

        return new ObstacleInfo
        {
            Found = true,
            Height = height,
            Depth = depth,
            WallPoint = wallPoint,
            LedgePoint = ledge,
            FarEdgePoint = farEdge,
            LandingPoint = landing,
            WallNormal = wallNormal,
            LandingClear = landingFound && IsClear(space, exclude, landing, landingShape),
            TopStandable = IsClear(space, exclude, ledge - wallNormal * Mathf.Min(depth * 0.5f, 0.4f), landingShape),
        };
    }

    /// <summary>
    /// Verifica che la parete sia piana anche ai lati del punto colpito.
    ///
    /// Tre raggi invece di uno: se uno dei laterali non trova nulla, o trova una faccia orientata
    /// molto diversamente, il raggio centrale aveva preso uno spigolo — e agganciarsi a uno spigolo
    /// significa scavalcare verso il nulla.
    /// </summary>
    private static bool ConfirmFlatWall(PhysicsDirectSpaceState3D space, Godot.Collections.Array<Rid> exclude,
        Vector3 chest, Vector3 forward, float reach, Vector3 wallNormal, Vector3 tangent)
    {
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 origin = chest + tangent * (CornerOffset * side);
            if (!Cast(space, exclude, origin, origin + forward * (reach + CornerOffset), out _, out Vector3 normal))
                return false;

            normal = new Vector3(normal.X, 0f, normal.Z).Normalized();
            if (normal.Dot(wallNormal) < CornerNormalTolerance)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Quanto e' spesso l'ostacolo: si avanza sulla sommita' a passi di <see cref="DepthStep"/>
    /// lanciando un raggio corto verso il basso, finche' il terreno sotto manca. Il punto in cui
    /// manca e' il bordo opposto.
    /// </summary>
    private static (float depth, Vector3 farEdge) MeasureDepth(PhysicsDirectSpaceState3D space,
        Godot.Collections.Array<Rid> exclude, Vector3 ledge, Vector3 inward, float maxDepth)
    {
        Vector3 current = ledge;

        for (float d = DepthStep; d <= maxDepth; d += DepthStep)
        {
            Vector3 probe = ledge + inward * d;
            if (!Cast(space, exclude, probe + Vector3.Up * 0.2f, probe + Vector3.Down * 0.3f,
                    out Vector3 surface, out _))
                return (d - DepthStep, current);

            current = surface;
        }

        return (maxDepth, current);
    }

    /// <summary>
    /// C'e' abbastanza spazio per starci in piedi? Una query di forma con la capsula del personaggio,
    /// appoggiata sul punto indicato. E' l'unico controllo che i raycast non possono dare: un raggio
    /// trova il suolo anche in fondo a un pertugio largo dieci centimetri.
    /// </summary>
    private static bool IsClear(PhysicsDirectSpaceState3D space, Godot.Collections.Array<Rid> exclude,
        Vector3 groundPoint, CapsuleShape3D? shape)
    {
        if (shape == null)
            return true;

        // Capsula leggermente ristretta: quella esatta tocca il pavimento e i muri a cui ci si puo'
        // legittimamente appoggiare, e nessun atterraggio risulterebbe mai libero.
        var probe = new CapsuleShape3D
        {
            Radius = Mathf.Max(shape.Radius - 0.05f, 0.05f),
            Height = Mathf.Max(shape.Height - 0.2f, 0.1f),
        };

        var query = new PhysicsShapeQueryParameters3D
        {
            ShapeRid = probe.GetRid(),
            Transform = new Transform3D(Basis.Identity, groundPoint + Vector3.Up * (probe.Height * 0.5f + 0.06f)),
            CollisionMask = ProbeMask,
            Exclude = exclude,
        };

        Godot.Collections.Array<Godot.Collections.Dictionary> hits = space.IntersectShape(query, 1);
        return hits.Count == 0;
    }

    /// Raycast sulla maschera della sonda. Restituisce falso se non colpisce nulla.
    private static bool Cast(PhysicsDirectSpaceState3D space, Godot.Collections.Array<Rid> exclude,
        Vector3 from, Vector3 to, out Vector3 position, out Vector3 normal)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, to, ProbeMask);
        query.Exclude = exclude;

        Godot.Collections.Dictionary hit = space.IntersectRay(query);
        if (hit.Count == 0)
        {
            position = Vector3.Zero;
            normal = Vector3.Zero;
            return false;
        }

        position = (Vector3)hit["position"];
        normal = (Vector3)hit["normal"];
        return true;
    }
}
