using Godot;

namespace Lagoon;

/// <summary>
/// Volume danneggiabile di un'entita': l'unica cosa che i raggi di tiro possono colpire.
///
/// Vive su un layer dedicato (<see cref="CollisionLayers.Hitbox"/>) e non maschera nulla: non
/// interroga mai la fisica, viene solo interrogata. Separare la hitbox dal corpo fisico
/// (<c>CharacterBody3D</c>/<c>StaticBody3D</c>) risolve strutturalmente due problemi del tiro:
///  - l'immunita' a se' stessi (il tiratore esclude il RID della propria hitbox dalla query);
///  - la distinzione fra "il proiettile si ferma qui" (mondo) e "questo subisce danno" (hitbox).
/// </summary>
public partial class HitboxComponent : Area3D
{
    /// Moltiplicatore di danno della zona colpita (1.0 = corpo). Predisposto per le zone
    /// differenziate (testa/arti): in Fase 3 esiste una sola hitbox per entita'.
    [Export] public float DamageMultiplier { get; set; } = 1.0f;

    /// Percorso al <see cref="HealthComponent"/> a cui inoltrare il danno.
    [Export] public NodePath HealthPath { get; set; } = new("../Health");

    public HealthComponent? Health { get; private set; }

    /// Peer proprietario, se la hitbox appartiene a un giocatore (0 per manichini e nemici).
    /// Serve alle regole di fuoco amico e all'attribuzione dei colpi.
    public int OwnerPeerId { get; private set; }

    public override void _Ready()
    {
        // Nessun monitoraggio: la hitbox non deve generare eventi di sovrapposizione, deve solo
        // essere raggiungibile da IntersectRay con CollideWithAreas = true.
        Monitoring = false;
        Monitorable = true;
        CollisionLayer = CollisionLayers.Hitbox;
        CollisionMask = 0;

        Health = GetNodeOrNull<HealthComponent>(HealthPath);
        if (Health == null)
            GD.PrintErr($"[HitboxComponent] Nessun HealthComponent in '{HealthPath}' sotto {GetPath()}.");

        // Il nome della root del Player e' l'id del peer proprietario (vedi PlayerController).
        Node? entityRoot = Health?.GetParent();
        if (entityRoot != null && int.TryParse(entityRoot.Name, out int peerId))
            OwnerPeerId = peerId;
    }

    /// Applica il danno gia' calcolato dall'host, includendo il moltiplicatore di zona.
    /// Il guard di autorita' vero e proprio vive in <see cref="HealthComponent.ApplyDamage"/>.
    public void ApplyDamage(float baseDamage, int attackerPeerId, Vector3 hitDirection = default)
    {
        int amount = Mathf.RoundToInt(baseDamage * DamageMultiplier);
        Health?.ApplyDamage(amount, attackerPeerId, hitDirection);
    }
}
