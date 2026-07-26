using Godot;

namespace Lagoon;

/// <summary>
/// Bersaglio placeholder per validare il tiro (CLAUDE.md §7: primitive, nessun asset finale).
/// Nessuna IA: non si muove e non reagisce, esiste solo per essere colpito.
///
/// E' piazzato staticamente nel livello e non spawnato a runtime: cosi' il NodePath e' identico su
/// ogni peer e il <c>MultiplayerSynchronizer</c> della salute funziona senza passare da un
/// <c>MultiplayerSpawner</c>.
///
/// La parte visiva (label, colore) gira su TUTTI i peer leggendo la salute replicata; il respawn
/// gira solo sull'host, che e' l'unico a poter mutare lo stato.
/// </summary>
public partial class TargetDummy : StaticBody3D
{
    /// Secondi prima che il manichino torni in piedi, cosi' il test e' ripetibile.
    [Export] public float RespawnSeconds { get; set; } = 6f;

    private HealthComponent _health = null!;
    private MeshInstance3D _mesh = null!;
    private Label3D _label = null!;
    private StandardMaterial3D _material = null!;

    private float _respawnTimer;
    private int _lastShownHealth = -1;

    private static readonly Color AliveColor = new(0.75f, 0.75f, 0.78f);
    private static readonly Color DeadColor = new(0.55f, 0.12f, 0.12f);

    public override void _Ready()
    {
        _health = GetNode<HealthComponent>("Health");
        _mesh = GetNode<MeshInstance3D>("MeshInstance3D");
        _label = GetNode<Label3D>("Label3D");

        CollisionLayer = CollisionLayers.Enemies;
        CollisionMask = CollisionLayers.World;

        // Materiale per istanza: i manichini condividono la mesh ma non il colore.
        _material = new StandardMaterial3D { AlbedoColor = AliveColor };
        _mesh.MaterialOverride = _material;

        RefreshVisual();
    }

    public override void _Process(double delta)
    {
        RefreshVisual();

        if (!_health.IsMultiplayerAuthority())
            return;

        // Respawn: solo l'host, unico a poter scrivere la salute replicata.
        if (!_health.IsDead)
        {
            _respawnTimer = 0f;
            return;
        }

        _respawnTimer += (float)delta;
        if (_respawnTimer >= RespawnSeconds)
        {
            _respawnTimer = 0f;
            _health.HostRevive();
        }
    }

    /// Riflette la salute replicata su label e colore. Idempotente: aggiorna solo se cambia.
    private void RefreshVisual()
    {
        if (_health.CurrentHealth == _lastShownHealth)
            return;

        _lastShownHealth = _health.CurrentHealth;
        _label.Text = $"{_health.CurrentHealth} / {_health.MaxHealth}";

        float t = _health.MaxHealth <= 0 ? 0f : (float)_health.CurrentHealth / _health.MaxHealth;
        _material.AlbedoColor = DeadColor.Lerp(AliveColor, t);
    }
}
