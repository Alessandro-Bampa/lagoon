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
/// L'origine del colpo si legge da <see cref="PlayerController.ResolvedSyncPosition"/> e non da
/// <c>SyncPosition</c>: dalla Fase 4 quest'ultima puo' essere espressa in coordinate locali a
/// un'imbarcazione, quindi usarla direttamente farebbe partire i colpi dall'origine dello scafo.
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
    /// Altezza della bocca dell'arma rispetto ai piedi dell'avatar.
    public const float MuzzleHeight = 1.1f;

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

    /// <summary>
    /// Contributo di RINCULO alla dispersione, in gradi, accumulato e riassorbito dall'host.
    /// Si replica questo e non la dispersione totale perche' il termine di distanza dipende da dove
    /// punta il cursore del singolo giocatore: il reticolo lo aggiunge in locale passando questo
    /// valore a <see cref="WeaponDefinition.SpreadDegrees"/>, cioe' alla stessa identica formula che
    /// usa l'host. Cosi' l'anello disegnato coincide col cono di tiro reale.
    /// </summary>
    [Export] public float RecoilSpread { get; set; }

    [Export] public bool Reloading { get; set; }

    // ====================================================================================
    //  Stato solo-host
    // ====================================================================================

    /// Caricatori per istanza d'arma: cambiando arma il caricatore precedente non si azzera.
    private readonly Dictionary<int, int> _magazines = new();
    private readonly RandomNumberGenerator _rng = new();

    private int _heldInstanceId;
    private ulong _lastShotMsec;
    private float _recoilSpread;
    private float _reloadRemaining;
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
            RecoilSpread = 0f;
            return;
        }

        // Riassorbimento del rinculo.
        _recoilSpread = Mathf.Max(
            0f, _recoilSpread - weapon.RecoilRecoveryDegreesPerSecond * (float)delta);
        RecoilSpread = _recoilSpread;

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
        if (requested is not (EquipSlotType.WeaponPrimary or EquipSlotType.WeaponSecondary
            or EquipSlotType.Sidearm))
            return;

        // Stesso slot gia' in mano: rinfodera.
        if (HeldSlot == slot && !string.IsNullOrEmpty(HeldItemId))
        {
            Holster();
            return;
        }

        ItemInstance? item = _inventory.Model.Equipment.Get(requested);
        if (item?.Definition is not WeaponDefinition weapon)
            return;

        HeldItemId = weapon.ItemId;
        HeldSlot = slot;
        _heldInstanceId = item.InstanceId;
        _recoilSpread = 0f;
        Reloading = false;
        _reloadRemaining = 0f;

        // Caricatore memorizzato per quest'istanza; un'arma mai impugnata parte carica.
        MagazineAmmo = _magazines.TryGetValue(item.InstanceId, out int stored)
            ? stored
            : weapon.MagazineSize;
        _magazines[item.InstanceId] = MagazineAmmo;

        RefreshReserve(weapon);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestFire(Vector3 aimPoint)
    {
        if (!ValidateSender())
            return;

        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null || !HoldStillValid())
            return;

        if (Reloading || MagazineAmmo <= 0 || !HandsFree())
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
        Vector3 origin = _player.ResolvedSyncPosition + Vector3.Up * MuzzleHeight;

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

        float spread = weapon.SpreadDegrees(aimDistance, _recoilSpread);
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

        MagazineAmmo--;
        _magazines[_heldInstanceId] = MagazineAmmo;
        _lastShotMsec = now;
        _recoilSpread = Mathf.Min(
            _recoilSpread + weapon.RecoilPerShotDegrees, weapon.MaxRecoilSpreadDegrees);

        Rpc(MethodName.BroadcastShot, origin, end, target != null);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestReload()
    {
        if (!ValidateSender())
            return;

        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null || !HoldStillValid() || Reloading || !HandsFree())
            return;
        if (MagazineAmmo >= weapon.MagazineSize)
            return;
        if (_inventory.Model.CountById(weapon.AmmoItemId) <= 0)
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
        Node? world = GetTree().GetFirstNodeInGroup(GameWorld.GroupName);
        if (world != null)
        {
            ShotEffects.SpawnTracer(world, origin, end);
            if (hit)
                ShotEffects.SpawnImpact(world, end);
        }

        var mount = _player.GetNodeOrNull<WeaponVisual>("Visual/WeaponMount");
        if (mount != null)
        {
            ShotEffects.SpawnMuzzleFlash(mount);
            mount.PlayKick();
        }

        bool isLocalShooter = _player.IsMultiplayerAuthority();
        if (isLocalShooter)
        {
            WeaponDefinition? weapon = HeldWeapon;
            var camera = _player.GetNodeOrNull<IsometricCamera>("PlayerCamera");
            if (camera != null && weapon != null)
                camera.AddKick(weapon.CameraKick);
        }

        EmitSignal(SignalName.ShotResolved, origin, end, hit, isLocalShooter);
    }

    // ====================================================================================
    //  Helper host
    // ====================================================================================

    /// Completa la ricarica: consuma le munizioni dalle griglie e rispinge l'inventario al client.
    private void FinishReload(WeaponDefinition weapon)
    {
        Reloading = false;
        _reloadRemaining = 0f;

        int needed = weapon.MagazineSize - MagazineAmmo;
        int taken = _inventory.Model.ConsumeById(weapon.AmmoItemId, needed);
        MagazineAmmo += taken;
        _magazines[_heldInstanceId] = MagazineAmmo;

        // Le griglie sono cambiate: il proprietario deve rivedere lo stack di munizioni calare.
        // HostPushState emette HostStateChanged, che aggiorna anche ReserveAmmo.
        _inventory.HostPushState();
    }

    /// <summary>
    /// True se l'arma impugnata e' ancora nel suo slot di equipaggiamento. Se il giocatore la
    /// sposta, la lascia cadere o la mette nello zaino mentre la impugna, il tiro deve decadere.
    /// </summary>
    private bool HoldStillValid()
    {
        ItemInstance? equipped = _inventory.Model.Equipment.Get((EquipSlotType)HeldSlot);
        if (equipped != null && equipped.InstanceId == _heldInstanceId)
            return true;

        Holster();
        return false;
    }

    /// <summary>
    /// True se le mani sono libere di usare l'arma. Durante uno scavalcamento o un'arrampicata non
    /// lo sono: reggono l'ostacolo (e infatti <c>VaultIkRig</c> le porta sul bordo e l'arma sparisce
    /// dalla vista), quindi sparare e ricaricare vanno rifiutati.
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
        RecoilSpread = 0f;
        _recoilSpread = 0f;
        Reloading = false;
        _reloadRemaining = 0f;
    }

    /// L'inventario autoritativo e' cambiato: rivalida l'impugnatura e riallinea la riserva.
    private void OnHostInventoryChanged()
    {
        WeaponDefinition? weapon = HeldWeapon;
        if (weapon == null || !HoldStillValid())
            return;

        RefreshReserve(weapon);
    }

    private void RefreshReserve(WeaponDefinition weapon)
        => ReserveAmmo = _inventory.Model.CountById(weapon.AmmoItemId);

    private bool ValidateSender()
    {
        if (!IsMultiplayerAuthority())
            return false; // solo l'host esegue davvero il calcolo
        int sender = Multiplayer.GetRemoteSenderId();
        // sender 0 = chiamata locale dell'host; altrimenti deve essere il proprietario dell'avatar.
        return sender == 0 || sender == _ownerPeerId;
    }
}
