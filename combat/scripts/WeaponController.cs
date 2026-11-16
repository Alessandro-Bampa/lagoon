using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Arma impugnata e tiro (Fase 3). Vive come figlio del Player ma — esattamente come
/// <see cref="PlayerInventory"/>, e a differenza del movimento — la sua autorita' e' SEMPRE l'host:
/// nessun client decide se ha colpito, quanto danno ha fatto o quante munizioni gli restano.
///
/// Realizzazione del pattern <c>RequestHit</c> di CLAUDE.md §3: il client invia il proprio INTENTO
/// (<see cref="RequestFire"/> con il solo punto di mira), l'host ricostruisce origine e direzione
/// dalla posizione replicata del tiratore, tira il dado della dispersione e ri-traccia il raggio.
/// Il client non nomina mai una vittima e non propone mai un ammontare di danno: se lo facesse,
/// falsificarlo sarebbe banale.
///
/// Stato replicato a TUTTI i peer (non solo al proprietario, a differenza dell'inventario): serve
/// agli altri per vedere chi impugna cosa e, per il proprietario, ad avere una HUD coerente.
///
/// Non c'e' reticolo: dal fianco si spara "a sentimento" con un cono largo, in mira (tasto
/// destro) le tacche dell'arma cadono al centro dello schermo e il cono si stringe.
///
/// L'origine del colpo sono gli OCCHI (<see cref="CharacterMotor.ResolvedEyePosition"/>): in prima
/// persona il colpo parte dal punto di vista, cosi' cio' che sta sotto al reticolo e' cio' che viene
/// colpito. Passa da <c>ResolvedSyncPosition</c> e non da <c>SyncPosition</c>, che dalla Fase 4 puo'
/// essere in coordinate locali a un'imbarcazione. La camera del client segue la testa animata e
/// sta a pochi centimetri dal punto nominale dell'host: lo scarto conta solo sparando rasente a
/// uno spigolo.
///
/// LIMITE NOTO (accettato in Fase 3): nessuna lag compensation. L'host ri-traccia dalla posizione
/// replicata del tiratore, che per un tiratore remoto e' vecchia fino a ~1 RTT.
/// Chi si muove veloce puo' vedere il tracciante partire leggermente dietro al proprio avatar, e un
/// bersaglio che si e' spostato durante l'RTT puo' essere mancato pur sembrando colpito sul client.
/// Il rimedio (buffer storico delle posizioni + rewind) va insieme alla validazione anti-cheat del
/// movimento, gia' rimandata in Fase 1.
/// </summary>
public partial class WeaponController : Node
{
    /// Emesso in locale su ogni peer quando arriva un colpo (per gli effetti e l'hit marker).
    [Signal]
    public delegate void ShotResolvedEventHandler(Vector3 origin, Vector3 end, bool hit, bool isLocalShooter);

    // ====================================================================================
    //  Stato replicato (MultiplayerSynchronizer figlio, visibilita' pubblica -> tutti i peer)
    // ====================================================================================

    /// ItemId dell'arma impugnata; stringa vuota = disarmato.
    [Export] public string HeldItemId { get; set; } = "";

    /// Slot di equipaggiamento da cui proviene l'arma impugnata (<see cref="EquipSlotType"/>).
    [Export] public int HeldSlot { get; set; }

    [Export] public int MagazineAmmo { get; set; }
    [Export] public int ReserveAmmo { get; set; }

    [Export] public bool Reloading { get; set; }

    // ====================================================================================
    //  Stato solo-host
    // ====================================================================================

    private readonly RandomNumberGenerator _rng = new();

    private int _heldInstanceId;
    private ulong _lastShotMsec;

    /// Dispersione accumulata sparando di fila, in gradi. Non si replica: nessun client la
    /// disegna (non c'e' reticolo), serve solo al dado dell'host.
    private float _bloom;
    private float _reloadRemaining;

    /// Tempo residuo dell'estrazione: finche' non scade l'arma non spara ne' ricarica.
    private float _drawRemaining;
    private int _rejectedShots;

    private int _ownerPeerId = NetworkConstants.HostPeerId;
    private ItemDatabase _db = null!;
    private PlayerInventory _inventory = null!;
    private PlayerController _player = null!;
    private HitboxComponent? _ownHitbox;

    /// Definizione dell'arma impugnata, o null se disarmato/id sconosciuto. Valida su ogni peer.
    public WeaponDefinition? HeldWeapon =>
        string.IsNullOrEmpty(HeldItemId) ? null : _db.Get(HeldItemId) as WeaponDefinition;

    public bool IsArmed => HeldWeapon != null;

    public override void _EnterTree()
    {
        // Sovrascrive il set ricorsivo per-peer di PlayerController._EnterTree: il tiro e'
        // autoritativo lato host. Ricorsivo, quindi copre anche il Sync figlio.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        _db = GetNode<ItemDatabase>("/root/ItemDatabase");
        _player = GetParent<PlayerController>();
        _inventory = _player.GetNode<PlayerInventory>("Inventory");
        _ownHitbox = _player.GetNodeOrNull<HitboxComponent>("Hitbox");
        _rng.Randomize();

        if (int.TryParse(_player.Name, out int peerId))
            _ownerPeerId = peerId;

        if (IsMultiplayerAuthority())
            _inventory.HostStateChanged += OnHostInventoryChanged;
    }

    public override void _Process(double delta)
    {
        if (!IsMultiplayerAuthority())
            return;

        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null)
        {
            _bloom = 0f;
            return;
        }

        _bloom = Mathf.Max(0f, _bloom - weapon.BloomRecoveryDegreesPerSecond * (float)delta);
        _drawRemaining = Mathf.Max(0f, _drawRemaining - (float)delta);

        if (!Reloading)
            return;

        _reloadRemaining -= (float)delta;
        if (_reloadRemaining <= 0f)
            FinishReload(weapon);
    }

    // ====================================================================================
    //  Submit: chiamati dal proprietario (WeaponInput). Host = locale; client = RpcId(host).
    // ====================================================================================

    /// Impugna l'arma nello slot indicato; ripetere lo stesso slot rinfodera.
    public void SubmitHold(EquipSlotType slot)
    {
        if (IsMultiplayerAuthority())
            RequestHold((int)slot);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestHold, (int)slot);
    }

    /// Spara verso un punto del mondo. Il punto e' un INTENTO: l'host lo valida e ricalcola tutto.
    public void SubmitFire(Vector3 aimPoint)
    {
        if (IsMultiplayerAuthority())
            RequestFire(aimPoint);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestFire, aimPoint);
    }

    public void SubmitReload()
    {
        if (IsMultiplayerAuthority())
            RequestReload();
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestReload);
    }

    // ====================================================================================
    //  Request: eseguite SOLO sull'host, sempre validate (§3.4)
    // ====================================================================================

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestHold(int slot)
    {
        if (!ValidateSender())
            return;

        var requested = (EquipSlotType)slot;
        if (!EquipRules.IsWeaponSlot(requested))
            return;

        // Stesso slot gia' in mano: rinfodera.
        if (HeldSlot == slot && !string.IsNullOrEmpty(HeldItemId))
        {
            Holster();
            return;
        }

        ItemInstance? item = _inventory.Model.Get(requested);
        if (item?.Definition is not WeaponDefinition weapon)
            return;

        HeldItemId = weapon.ItemId;
        HeldSlot = slot;
        _heldInstanceId = item.InstanceId;
        _bloom = 0f;
        Reloading = false;
        _reloadRemaining = 0f;
        _drawRemaining = weapon.DrawSeconds;

        // Il caricatore e' un item montato nell'arma: i colpi sono i suoi.
        RefreshAmmo(weapon);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestFire(Vector3 aimPoint)
    {
        if (!ValidateSender())
            return;

        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null || !HoldStillValid())
            return;

        if (Reloading || _drawRemaining > 0f || MagazineAmmo <= 0 || !HandsFree())
            return;

        // Cadenza di fuoco: il 10% di tolleranza assorbe il jitter di rete senza aprire la porta
        // a un client che spara al doppio del rateo.
        ulong now = Time.GetTicksMsec();
        if (now - _lastShotMsec < (ulong)(weapon.ShotIntervalMsec * 0.9f))
        {
            _rejectedShots++;
            return;
        }

        if (!aimPoint.IsFinite())
        {
            _rejectedShots++;
            return;
        }

        // Origine SEMPRE ricavata dallo stato replicato del tiratore, mai fornita dal client, e mai
        // da GlobalPosition (che sugli avatar remoti e' il risultato dell'interpolazione).
        Vector3 origin = _player.ResolvedEyePosition;

        Vector3 toAim = aimPoint - origin;
        if (toAim.LengthSquared() < 0.0001f)
            return;

        float aimDistance = toAim.Length();
        Vector3 direction = toAim / aimDistance;

        // Fuori portata: si clampa invece di rifiutare. Mirare al cielo e' legittimo, il colpo
        // semplicemente non arriva cosi' lontano.
        if (aimDistance > weapon.MaxRangeMeters)
        {
            aimDistance = weapon.MaxRangeMeters;
            _rejectedShots++;
        }

        // Il cono dipende dalla postura del tiratore, letta dallo stato REPLICATO: in mira e'
        // stretto; dal fianco, in movimento e in aria si allarga. SyncAiming lo pubblica il
        // client come il resto del suo movimento (client-autoritativo in questa fase): mentire
        // vale un cono piu' stretto, non un colpo che l'host non avrebbe tracciato.
        float moveFraction = _player.SyncLocalVelocity.Length() / Mathf.Max(_player.RunSpeed, 0.001f);
        float spread = weapon.SpreadDegrees(
            _player.SyncAiming, _player.SyncCrouching, _player.SyncGrounded, moveFraction, _bloom);
        Vector3 shotDir = AimResolver.ApplySpread(direction, spread, _rng);

        HitboxComponent? target = AimResolver.TraceShot(
            _player.GetWorld3D(), origin, shotDir, weapon.MaxRangeMeters,
            _ownHitbox?.GetRid() ?? default, out Vector3 end);

        if (target != null)
        {
            float distance = origin.DistanceTo(end);
            // La DIREZIONE del colpo (di volo, mondo) viaggia col danno: e' cio' che alimenta la
            // hit reaction animata del bersaglio. E' un dato calcolato QUI, host-side — mai
            // qualcosa che arriva dal client.
            target.ApplyDamage(weapon.Damage * weapon.DamageFactorAt(distance), _ownerPeerId, shotDir);
        }

        // Il colpo esce dalla cima del caricatore montato (LIFO). Niente push dell'inventario a
        // ogni colpo: MagazineAmmo e' gia' replicato, la tile si riallinea al prossimo push.
        HeldInstance()?.MountedMagazine?.PopRound();
        MagazineAmmo = HeldInstance()?.MountedMagazine?.RoundCount ?? 0;
        _lastShotMsec = now;
        _bloom = Mathf.Min(_bloom + weapon.BloomPerShotDegrees, weapon.MaxBloomDegrees);

        Rpc(MethodName.BroadcastShot, origin, end, target != null);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestReload()
    {
        if (!ValidateSender())
            return;

        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null || !HoldStillValid() || Reloading || _drawRemaining > 0f || !HandsFree())
            return;
        // Ricaricare = cambiare caricatore: ne serve uno di scorta, piu' pieno di quello montato.
        ItemInstance? spare = FindSpareMagazine(weapon);
        if (spare == null || spare.RoundCount <= MagazineAmmo)
            return;

        Reloading = true;
        _reloadRemaining = weapon.ReloadSeconds;
    }

    // ====================================================================================
    //  Estetica: host -> tutti (§3.3). Nessun effetto sullo stato di gioco.
    // ====================================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    public void BroadcastShot(Vector3 origin, Vector3 end, bool hit)
    {
        bool isLocalShooter = _player.IsMultiplayerAuthority();

        // Vampa e tracciante partono dalla BOCCA dell'arma che si vede, non dall'origine vera
        // (gli occhi): dal punto di vista sarebbe un segmento visto di punta. Per chi spara e'
        // l'arma del viewmodel, per tutti gli altri quella del corpo in terza persona.
        var viewmodel = _player.GetNodeOrNull<ViewmodelAnimator>("PlayerCamera/Viewmodel");
        var body = _player.GetNodeOrNull<CharacterAnimator>("Visual/CharacterRig");

        Vector3 muzzle = origin;
        if (isLocalShooter && viewmodel is { HasWeapon: true, Muzzle: not null })
        {
            muzzle = viewmodel.MuzzlePosition;
            ShotEffects.SpawnMuzzleFlash(viewmodel.Muzzle);
        }
        else if (body is { Hold.HasWeapon: true })
        {
            muzzle = body.Hold.MuzzlePosition;
            ShotEffects.SpawnMuzzleFlash(body.Hold, body.Hold.ToLocal(muzzle));
        }

        Node? world = GetTree().GetFirstNodeInGroup(GameWorld.GroupName);
        if (world != null)
        {
            ShotEffects.SpawnTracer(world, muzzle, end);
            if (hit)
                ShotEffects.SpawnImpact(world, end);
        }

        if (isLocalShooter)
            ApplyViewRecoil();

        EmitSignal(SignalName.ShotResolved, origin, end, hit, isLocalShooter);
    }

    /// <summary>
    /// Rinculo della visuale del tiratore locale: la camera sale e scarta, e rientra da sola.
    /// E' pura resa, ma sposta il centro dello schermo e quindi il punto mirato del colpo dopo:
    /// e' il rinculo che si compensa col mouse. L'host non ne sa nulla, riceve solo la mira.
    /// </summary>
    private void ApplyViewRecoil()
    {
        WeaponDefinition? weapon = HeldWeapon;
        var camera = _player.GetNodeOrNull<FirstPersonCamera>("PlayerCamera");
        if (camera == null || weapon == null)
            return;

        float factor = 1f;
        if (_player.SyncAiming)
            factor *= weapon.AimRecoilFactor;
        if (_player.SyncCrouching)
            factor *= weapon.CrouchRecoilFactor;

        camera.AddRecoil(
            weapon.RecoilPitchDegrees * factor,
            (weapon.RecoilYawBiasDegrees + _rng.RandfRange(-weapon.RecoilYawDegrees, weapon.RecoilYawDegrees)) * factor,
            _rng.RandfRange(-weapon.RecoilRollDegrees, weapon.RecoilRollDegrees) * factor,
            weapon.RecoilSnappiness,
            weapon.RecoilReturnSpeed);
    }

    // ====================================================================================
    //  Helper host
    // ====================================================================================

    /// <summary>
    /// Completa la ricarica: il caricatore di scorta piu' pieno (da rig o tasche) entra nell'arma e
    /// quello vecchio prende il suo posto. E' la stessa operazione di un trascinamento del
    /// caricatore sull'arma, quindi passa da <see cref="InventoryOps"/> e ne eredita le regole.
    /// </summary>
    private void FinishReload(WeaponDefinition weapon)
    {
        Reloading = false;
        _reloadRemaining = 0f;

        ItemInstance? held = HeldInstance();
        ItemInstance? spare = FindSpareMagazine(weapon);
        InventoryOps.Session? session = _inventory.OpenSession();
        if (held == null || spare == null || session == null)
            return;

        InventoryOps.DropOnItem(
            session, ItemAddress.PlayerSpace, spare.InstanceId, ItemAddress.PlayerSpace, held.InstanceId);
        session.Commit();

        // HostPushState emette HostStateChanged, che riallinea MagazineAmmo e ReserveAmmo.
        _inventory.HostPushState();
    }

    /// Istanza dell'arma impugnata, se e' ancora nel suo slot.
    private ItemInstance? HeldInstance()
    {
        ItemInstance? equipped = _inventory.Model.Get((EquipSlotType)HeldSlot);
        return equipped != null && equipped.InstanceId == _heldInstanceId ? equipped : null;
    }

    /// Caricatore di scorta compatibile piu' pieno fra quelli a portata di mano (rig e tasche).
    private ItemInstance? FindSpareMagazine(WeaponDefinition weapon)
    {
        ItemInstance? best = null;
        foreach (ItemInstance item in _inventory.Model.QuickAccessItems())
        {
            if (item.Definition is not MagazineDefinition magazine || magazine.Caliber != weapon.Caliber)
                continue;
            if (item.RoundCount > 0 && (best == null || item.RoundCount > best.RoundCount))
                best = item;
        }
        return best;
    }

    /// <summary>
    /// True se l'arma impugnata e' ancora nel suo slot di equipaggiamento. Se il giocatore la
    /// sposta, la lascia cadere o la mette nello zaino mentre la impugna, il tiro deve decadere.
    /// </summary>
    private bool HoldStillValid()
    {
        if (HeldInstance() != null)
            return true;

        Holster();
        return false;
    }

    /// <summary>
    /// True se le mani sono libere di usare l'arma. Durante uno scavalcamento o un'arrampicata non
    /// lo sono: reggono l'ostacolo (e infatti l'arma esce dalla vista), quindi sparare e ricaricare
    /// vanno rifiutati.
    ///
    /// Si legge <see cref="CharacterMotor.SyncVaulting"/>, cioe' lo stato REPLICATO, e non
    /// <c>Vaulting</c>: il movimento e' client-autoritativo, quindi per un tiratore remoto l'host non
    /// esegue la manovra e lo stato locale sarebbe sempre falso. Vale lo stesso limite di §7 della
    /// skill (nessuna lag compensation): lo stato e' vecchio fino a ~1 RTT, il che al piu' sposta di
    /// un'inezia i bordi della finestra in cui il colpo viene rifiutato.
    ///
    /// Non e' un veto sull'IMPUGNATURA: <c>RequestHold</c> non lo consulta, perche' cambiare slot non
    /// produce un colpo e rifiutarlo lascerebbe l'host e il client in disaccordo su cosa si ha in
    /// mano. Il rinfodero implicito del parkour resta quello che era, cioe' puramente visivo
    /// (skill character-animation §8): slot e caricatore non si toccano.
    /// </summary>
    private bool HandsFree() => !_player.SyncVaulting;

    private void Holster()
    {
        HeldItemId = "";
        HeldSlot = 0;
        _heldInstanceId = 0;
        MagazineAmmo = 0;
        ReserveAmmo = 0;
        _bloom = 0f;
        Reloading = false;
        _reloadRemaining = 0f;
        _drawRemaining = 0f;
    }

    /// L'inventario autoritativo e' cambiato: rivalida l'impugnatura e riallinea la riserva.
    private void OnHostInventoryChanged()
    {
        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null || !HoldStillValid())
            return;

        RefreshAmmo(weapon);
    }

    /// <summary>
    /// Riallinea i due contatori replicati allo stato dell'inventario: i colpi nel caricatore
    /// montato e la riserva, cioe' i colpi nei caricatori di scorta a portata di mano. Le cartucce
    /// sfuse non contano: prima vanno caricate in un caricatore.
    /// </summary>
    private void RefreshAmmo(WeaponDefinition weapon)
    {
        MagazineAmmo = HeldInstance()?.MountedMagazine?.RoundCount ?? 0;

        int reserve = 0;
        foreach (ItemInstance item in _inventory.Model.QuickAccessItems())
            if (item.Definition is MagazineDefinition magazine && magazine.Caliber == weapon.Caliber)
                reserve += item.RoundCount;
        ReserveAmmo = reserve;
    }

    private bool ValidateSender()
    {
        if (!IsMultiplayerAuthority())
            return false; // solo l'host esegue davvero il calcolo
        int sender = Multiplayer.GetRemoteSenderId();
        // sender 0 = chiamata locale dell'host; altrimenti deve essere il proprietario dell'avatar.
        return sender == 0 || sender == _ownerPeerId;
    }
}
