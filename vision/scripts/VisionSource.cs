using Godot;
using RidArray = Godot.Collections.Array<Godot.Rid>;

namespace Lagoon;

/// <summary>
/// Campo visivo di un personaggio: cosa puo' vedere in questo istante. Componente CONDIVISO fra
/// giocatore e NPC, come <see cref="CharacterMotor"/> lo e' per il movimento — va montato come
/// figlio del root del personaggio e legge il motore genitore.
///
/// LA REGOLA DA NON ROMPERE: <see cref="CanSeePoint"/> e' la FONTE DI VERITA'. Il ventaglio di
/// raggi (<see cref="Radii"/>) e' solo la sua discretizzazione, e da qui esce UNA SOLA VOLTA — nella
/// maschera polare di <see cref="VisionMask"/> — per servire tutti i consumatori a schermo: lo
/// shroud (cosa e' al buio) e il materiale di mondo (quali superfici si aprono).
///
/// Non esistono due raggi, due aperture o due maschere di collisione. Se il rendering avesse
/// parametri propri si otterrebbero nemici visibili a schermo ma "non visti" dal gioco, o superfici
/// che si aprono su terreno che resta nero. La sola incoerenza accettata e' la risoluzione angolare
/// del ventaglio (<see cref="RayCount"/>).
///
/// RETE: questo nodo non ha stato proprio da replicare e non contiene nessuna RPC. Legge SOLO
/// proprieta' gia' replicate del motore (posizione, imbardata della mira, stance di mira),
/// esattamente come <c>NpcAnimationBridge</c>, quindi produce lo stesso risultato su ogni peer a
/// partire dallo stesso stato. La visione e' INDIVIDUALE: ogni giocatore vede il proprio cono, e
/// nessuna decisione presa qui tocca lo stato di gioco (CLAUDE.md §3).
/// </summary>
public partial class VisionSource : Node3D
{
    /// Gruppo della VisionSource dell'avatar LOCALE. Vedi <see cref="VisionRegistry"/>.
    public const string LocalGroupName = "local_vision";

    // ====================================================================================
    //  Profilo passivo (a riposo / in movimento): corto e a giro completo
    // ====================================================================================

    /// Raggio a riposo, in metri.
    [Export] public float PassiveRadius { get; set; } = 12f;

    /// <summary>
    /// Apertura a riposo, in gradi (totale, non per lato). A 360 non esiste alcun settore cieco:
    /// a riposo si vede tutt'intorno, e il limite della visione e' solo la DISTANZA.
    ///
    /// E' una scelta di gioco deliberata: il cuneo cieco alle spalle rendeva il movimento
    /// scomodo senza aggiungere tensione utile — in vista dall'alto il giocatore non ha un modo
    /// naturale di "girarsi a controllare", perche' il corpo insegue il cursore. Il compromesso
    /// rischio/ricompensa resta tutto sulla MIRA, dove il cono si stringe a
    /// <see cref="AimFovDegrees"/>.
    /// </summary>
    [Export] public float PassiveFovDegrees { get; set; } = 360f;

    // ====================================================================================
    //  Profilo di mira: lungo e stretto
    // ====================================================================================

    /// Raggio in mira, in metri.
    [Export] public float AimRadius { get; set; } = 30f;

    /// Apertura in mira, in gradi.
    [Export] public float AimFovDegrees { get; set; } = 34f;

    // ====================================================================================
    //  Bolla periferica: sempre attiva, anche in mira
    // ====================================================================================

    /// <summary>
    /// Raggio della bolla ravvicinata, SEMPRE attiva e sommata al cono.
    ///
    /// E' cio' che regge la visione mentre si mira: senza, il cono stretto renderebbe ciechi a un
    /// metro e un nemico potrebbe arrivare addosso dal nulla. Il compromesso della mira resta (si
    /// perde la consapevolezza a media distanza sui fianchi) ma non diventa punitivo al punto da
    /// scoraggiare la mira stessa.
    /// </summary>
    [Export] public float PeripheralRadius { get; set; } = 6f;

    /// <summary>
    /// Apertura della bolla periferica, in gradi. A 360 la bolla non ha settori ciechi, quindi
    /// nemmeno in piena mira esiste una direzione a visibilita' zero: alle spalle si vede
    /// comunque fino a <see cref="PeripheralRadius"/>.
    /// </summary>
    [Export] public float PeripheralFovDegrees { get; set; } = 360f;

    // ====================================================================================
    //  Parametri comuni
    // ====================================================================================

    /// Velocita' di interpolazione fra profilo passivo e profilo di mira (frazione al secondo).
    [Export] public float BlendSpeed { get; set; } = 4f;

    /// <summary>
    /// Numero di raggi del ventaglio, e quindi larghezza della texture polare dello shroud.
    /// Va tenuto potenza di due e coerente con <c>ShroudRenderer</c>, che lo legge da qui.
    /// </summary>
    [Export] public int RayCount { get; set; } = 256;

    /// <summary>
    /// Quanto il raggio "sfonda" oltre la superficie che lo ferma, in metri. **Solo per il
    /// rendering.**
    ///
    /// Serve perche' la superficie che ti blocca la vista TU LA VEDI: e' il muro che stai
    /// guardando. Senza questo margine il punto d'impatto cade esattamente su <c>r</c>, cioe' in
    /// mezzo alla sfumatura <c>smoothstep(r - edge_softness, r, dist)</c> dello shader, e la faccia
    /// del muro si ritrova in ombra sotto la propria ombra. Va tenuto >= <c>edge_softness</c> del
    /// materiale, altrimenti la sfumatura ricade comunque sulla superficie.
    ///
    /// Il prezzo e' una perdita di luce di questa entita' DIETRO l'ostacolo, che a spessori
    /// normali resta nascosta dall'ostacolo stesso. La query puntuale non lo usa: il gate resta
    /// esatto, il margine e' un fatto di sola resa.
    /// </summary>
    [Export] public float SurfaceBias { get; set; } = 1.0f;

    /// <summary>
    /// Quota a cui corrono i raggi di visione. E' la stessa costante usata dalla mira e dalla bocca
    /// dell'arma (<see cref="AimResolver.ChestHeight"/>): guardare, mirare e sparare devono partire
    /// dallo stesso punto, altrimenti si vede qualcosa che non si puo' colpire.
    /// </summary>
    [Export] public float EyeHeight { get; set; } = AimResolver.ChestHeight;

    /// <summary>
    /// Se calcolare il ventaglio completo. Vero solo per l'avatar locale: il poligono serve
    /// unicamente allo shroud a schermo. Su avatar remoti e NPC resta falso e non si spende un solo
    /// raycast — ma <see cref="CanSeePoint"/> continua a funzionare, perche' e' indipendente.
    /// </summary>
    public bool ComputeFan { get; private set; }

    /// <summary>
    /// Lunghezza di ciascun raggio, in metri. Indice i = settore angolare
    /// <c>theta_i = -PI + (i + 0.5) * TAU / RayCount</c>, nella convenzione dello SHADER
    /// (<c>atan2(z, x)</c>), non in quella del gioco. Vedi <see cref="ShaderAngleOf"/>.
    /// </summary>
    public float[] Radii { get; private set; } = [];

    /// Raggio massimo raggiungibile da questa sorgente: la scala della texture polare.
    public float MaxRange => Mathf.Max(Mathf.Max(PassiveRadius, AimRadius), PeripheralRadius);

    /// Posizione dell'occhio in coordinate mondo, ricostruita dallo stato REPLICATO.
    public Vector3 EyePosition => _motor.ResolvedSyncPosition + Vector3.Up * EyeHeight;

    private CharacterMotor _motor = null!;
    private float _aimBlend;
    private Rid _selfHitbox;

    // Memoria per saltare il ricalcolo quando non e' cambiato nulla di percettibile.
    private Vector3 _lastFanOrigin = Vector3.Inf;
    private float _lastFanYaw = float.NaN;
    private float _lastFanBlend = float.NaN;

    /// Soglie sotto le quali il ventaglio precedente e' ancora valido.
    private const float RecomputePositionEpsilon = 0.05f;   // metri
    private const float RecomputeAngleEpsilon = 0.01f;      // radianti (~0.6 gradi)

    public override void _Ready()
    {
        _motor = GetParent<CharacterMotor>();
        Radii = new float[Mathf.Max(RayCount, 8)];

        // "Avatar locale" = il root ha autorita' del peer proprietario. Stesso criterio di
        // PlayerHud e PlayerNetworkSync. Per un NPC (host-autoritativo) e' vero solo sull'host,
        // che infatti non deve disegnare nessuno shroud per lui: per questo il gruppo si aggiunge
        // solo se il motore e' un giocatore.
        ComputeFan = _motor is PlayerController && _motor.IsMultiplayerAuthority();

        if (ComputeFan)
            AddToGroup(LocalGroupName);

        // La propria hitbox non deve occludere la propria vista. Non e' in VisionBlockerMask, ma
        // escluderla esplicitamente rende il codice robusto se un giorno la maschera cambiasse.
        if (_motor.GetNodeOrNull<Area3D>("Hitbox") is { } hitbox)
            _selfHitbox = hitbox.GetRid();
    }

    public override void _PhysicsProcess(double delta)
    {
        // L'interpolazione passivo<->mira gira su OGNI sorgente, anche senza ventaglio: serve
        // anche a CanSeePoint, che deve usare la stessa forma che si vede a schermo.
        float target = _motor.SyncAiming ? 1f : 0f;
        _aimBlend = Mathf.Lerp(_aimBlend, target, Mathf.Clamp((float)delta * BlendSpeed, 0f, 1f));

        if (ComputeFan)
            UpdateFan();
    }

    // ====================================================================================
    //  Query puntuale: la fonte di verita'
    // ====================================================================================

    /// <summary>
    /// Se il punto dato e' visibile adesso: dentro il cono corrente OPPURE dentro la bolla
    /// periferica, e senza occlusori sulla linea diretta. Un solo raycast, indipendente dal
    /// ventaglio: si puo' chiamare su qualunque VisionSource, anche su quelle che non calcolano
    /// nulla per il rendering.
    /// </summary>
    public bool CanSeePoint(Vector3 worldPoint)
    {
        Vector3 eye = EyePosition;

        // Tutto il ragionamento e' sul piano XZ: in una visuale dall'alto un cono inclinato
        // verticalmente non sarebbe ne' leggibile ne' desiderabile, e SyncAimPitch va ignorato.
        Vector2 flat = new(worldPoint.X - eye.X, worldPoint.Z - eye.Z);
        float distance = flat.Length();
        if (distance < 0.001f)
            return true;

        float angle = Mathf.Atan2(flat.Y, flat.X);
        if (distance > RadiusAt(angle))
            return false;

        // L'occlusione si verifica verso il punto REALE, quota compresa, senza alcun rialzo: il
        // raggio deve poter scendere. Un tempo la quota era clampata a eye.Y - 1 e il risultato era
        // che dal piano di sopra si vedeva quello di sotto — il raggio restava sopra il solaio
        // invece di attraversarlo. Il caso che quel clamp voleva salvare (un bersaglio in piedi
        // dietro un muretto basso) e' gia' coperto da CanSee, che mira all'altezza dell'occhio.
        return !IsBlocked(eye, worldPoint);
    }

    /// <summary>
    /// Se il personaggio dato e' visibile. Si mira all'altezza dell'occhio e non all'origine del
    /// nodo, che per un personaggio sta ai PIEDI: un bersaglio in piedi dietro un muretto basso
    /// deve essere visto, ed e' il caso normale.
    /// </summary>
    public bool CanSee(Node3D target)
    {
        Vector3 at = target is CharacterMotor motor ? motor.ResolvedSyncPosition : target.GlobalPosition;
        return CanSeePoint(at + Vector3.Up * EyeHeight);
    }

    // NOTA: qui stava ClearReachAt, che leggeva il ventaglio togliendo SurfaceBias. Non serve piu' a
    // nessuno lato C#: la sottrazione e' scesa nello shader, dentro `vision_visibility`
    // (vision/shaders/vision.gdshaderinc), dove ora vive l'unica copia della regola. Chi tornasse a
    // leggere Radii da C# deve ricordarsi di rifarla: chi si tiene il bias tratta come visibile un
    // metro DIETRO ogni muro.

    /// <summary>
    /// Raggio di visione all'angolo dato (convenzione shader). E' l'UNICO punto in cui si combinano
    /// cono e bolla: sia la query puntuale sia il ventaglio passano di qui, quindi non possono
    /// divergere per costruzione.
    /// </summary>
    public float RadiusAt(float shaderAngle)
    {
        float facing = ShaderAngleOf(_motor.SyncAimYaw);
        float offset = Mathf.Abs(Mathf.AngleDifference(facing, shaderAngle));

        float coneRadius = Mathf.Lerp(PassiveRadius, AimRadius, _aimBlend);
        float coneFov = Mathf.Lerp(PassiveFovDegrees, AimFovDegrees, _aimBlend);

        float radius = Covers(coneFov, offset) ? coneRadius : 0f;

        // Unione, non sostituzione: la bolla ravvicinata resta attiva anche in piena mira.
        if (Covers(PeripheralFovDegrees, offset))
            radius = Mathf.Max(radius, PeripheralRadius);

        return radius;
    }

    /// <summary>
    /// Se un'apertura copre lo scostamento angolare dato.
    ///
    /// Il caso "giro completo" e' esplicito e non affidato al confronto: a 360 gradi la semi-
    /// apertura vale esattamente PI, cioe' il massimo che <see cref="Mathf.AngleDifference"/> puo'
    /// restituire, e un arrotondamento in eccesso azzererebbe il singolo raggio dritto alle spalle
    /// — una spina nera dietro il giocatore, che si legge come un guasto e non come un epsilon.
    /// </summary>
    private static bool Covers(float fovDegrees, float offsetRadians)
        => fovDegrees >= 360f || offsetRadians <= Mathf.DegToRad(fovDegrees) * 0.5f;

    /// <summary>
    /// Converte un'imbardata del GIOCO in un angolo dello SHADER.
    ///
    /// Il gioco misura l'imbardata come <c>Atan2(dir.X, dir.Z)</c> (X per primo, vedi
    /// <c>PlayerController.UpdateAiming</c>); lo shader e il ventaglio usano <c>atan2(z, x)</c>,
    /// la convenzione matematica standard. Le due differiscono di un quarto di giro con segno
    /// invertito. Sbagliare questa conversione produce un cono ruotato di 90 gradi, che si legge
    /// come "il sistema non funziona" invece che come un errore di segno: e' il motivo per cui la
    /// conversione sta in un metodo con un nome, e non sparsa nelle formule.
    /// </summary>
    public static float ShaderAngleOf(float gameYaw) => Mathf.Pi * 0.5f - gameYaw;

    // ====================================================================================
    //  Ventaglio per lo shroud
    // ====================================================================================

    private void UpdateFan()
    {
        Vector3 origin = EyePosition;
        float yaw = _motor.SyncAimYaw;

        // Se non e' cambiato nulla di percettibile, il ventaglio precedente e' ancora valido:
        // stare fermi non deve costare 128 raycast al frame.
        if (origin.DistanceSquaredTo(_lastFanOrigin) < RecomputePositionEpsilon * RecomputePositionEpsilon
            && Mathf.Abs(Mathf.AngleDifference(_lastFanYaw, yaw)) < RecomputeAngleEpsilon
            && Mathf.Abs(_lastFanBlend - _aimBlend) < 0.001f)
            return;

        _lastFanOrigin = origin;
        _lastFanYaw = yaw;
        _lastFanBlend = _aimBlend;

        int count = Radii.Length;
        var space = GetWorld3D().DirectSpaceState;

        for (int i = 0; i < count; i++)
        {
            float angle = -Mathf.Pi + (i + 0.5f) * Mathf.Tau / count;
            float reach = RadiusAt(angle);

            if (reach <= 0f)
            {
                Radii[i] = 0f;
                continue;
            }

            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Radii[i] = CastRay(space, origin, direction, reach);
        }
    }

    /// Distanza libera nella direzione data, fino a <paramref name="reach"/>.
    private float CastRay(PhysicsDirectSpaceState3D space, Vector3 origin, Vector3 direction, float reach)
    {
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * reach);
        query.CollisionMask = CollisionLayers.VisionBlockerMask;
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        if (_selfHitbox.IsValid)
            query.Exclude = new RidArray { _selfHitbox };

        var hit = space.IntersectRay(query);
        if (hit.Count == 0)
            return reach;

        // Si supera il punto d'impatto di SurfaceBias, cosi' la superficie colpita cade DENTRO la
        // zona illuminata e non dentro la propria sfumatura di bordo. Vedi SurfaceBias.
        float distance = origin.DistanceTo((Vector3)hit["position"]);
        return Mathf.Min(distance + SurfaceBias, reach);
    }

    /// Se qualcosa interrompe la linea fra i due punti.
    private bool IsBlocked(Vector3 from, Vector3 to)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollisionMask = CollisionLayers.VisionBlockerMask;
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        if (_selfHitbox.IsValid)
            query.Exclude = new RidArray { _selfHitbox };

        return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
    }
}
