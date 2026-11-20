using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Cosa indossa un personaggio, visto da TUTTI i peer: gli <c>ItemId</c> dei
/// <see cref="WearableDefinition"/> indossati, ordinati per slot. Stato host-autoritativo replicato
/// dal <c>MultiplayerSynchronizer</c> figlio (CLAUDE.md §3).
///
/// Serve perche' l'inventario viaggia solo verso il proprietario: gli altri giocatori non sanno che
/// elmetto porti, ma devono vederlo. Nessuna RPC nuova: indossare e togliere passano dagli intenti
/// dell'inventario gia' validati dall'host (<c>Move</c>, <c>QuickEquip</c>); qui si pubblica solo il
/// risultato. La resa la fa <see cref="CharacterOutfit"/>, letta dai bridge di animazione.
///
/// Giocatore: l'host ricalcola a ogni <see cref="PlayerInventory.HostStateChanged"/> dal fratello
/// <c>Inventory</c>. NPC (nessun inventario): l'host pubblica <see cref="DefaultWornItemIds"/>.
/// </summary>
public partial class CharacterAppearance : Node
{
    [Signal] public delegate void AppearanceChangedEventHandler();

    private string[] _worn = Array.Empty<string>();

    /// Replicato (on change). Scritto SOLO dall'host.
    [Export]
    public string[] WornItemIds
    {
        get => _worn;
        set
        {
            value ??= Array.Empty<string>();
            if (SameAs(value))
                return;
            _worn = value;
            if (IsInsideTree())
                EmitSignal(SignalName.AppearanceChanged);
        }
    }

    /// Abiti di un personaggio senza inventario (NPC), pubblicati dall'host all'avvio.
    [Export] public string[] DefaultWornItemIds { get; set; } = Array.Empty<string>();

    private PlayerInventory? _inventory;

    public override void _EnterTree()
    {
        // Come WeaponController e HealthComponent: sovrascrive il marchio per-peer del giocatore.
        // Ricorsivo, quindi copre anche il Sync figlio.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        if (!IsMultiplayerAuthority())
            return;

        _inventory = GetParent().GetNodeOrNull<PlayerInventory>("Inventory");
        if (_inventory != null)
        {
            _inventory.HostStateChanged += RefreshFromInventory;
            RefreshFromInventory();
        }
        else
        {
            WornItemIds = DefaultWornItemIds;
        }
    }

    public override void _ExitTree()
    {
        if (_inventory != null)
            _inventory.HostStateChanged -= RefreshFromInventory;
    }

    /// Solo host: gli oggetti indossabili negli slot del personaggio, nell'ordine di
    /// <see cref="EquipRules.AllSlots"/> (stabile: stessa lista, stesso array).
    private void RefreshFromInventory()
    {
        if (_inventory == null)
            return;
        var ids = new List<string>();
        foreach (EquipSlotType slot in EquipRules.AllSlots)
        {
            if (_inventory.Model.Get(slot)?.Definition is WearableDefinition wearable)
                ids.Add(wearable.ItemId);
        }
        WornItemIds = ids.ToArray();
    }

    private bool SameAs(string[] other)
    {
        if (other.Length != _worn.Length)
            return false;
        for (int i = 0; i < other.Length; i++)
        {
            if (other[i] != _worn[i])
                return false;
        }
        return true;
    }
}
