using Godot;

namespace Lagoon;

/// <summary>
/// Radice della scena di gioco. Gestisce lo spawn/despawn degli avatar reagendo agli eventi peer
/// del <see cref="NetworkManager"/> (via <see cref="EventBus"/>). Lo spawn avviene SOLO sull'host
/// (autorita' server, CLAUDE.md §3): il <c>MultiplayerSpawner</c> replica automaticamente gli avatar
/// a tutti i client, anche a chi entra in ritardo.
/// </summary>
public partial class GameWorld : Node3D
{
    private const string PlayerScenePath = "res://player/scenes/Player.tscn";

    private PackedScene _playerScene = null!;
    private Node3D _players = null!;
    private EventBus _eventBus = null!;

    public override void _Ready()
    {
        _playerScene = GD.Load<PackedScene>(PlayerScenePath);
        _players = GetNode<Node3D>("Players");
        _eventBus = GetNode<EventBus>("/root/EventBus");

        _eventBus.PeerJoined += OnPeerJoined;
        _eventBus.PeerLeft += OnPeerLeft;
    }

    private void OnPeerJoined(long peerId)
    {
        // Doppia guardia: solo l'host istanzia gli avatar.
        if (!Multiplayer.IsServer())
            return;

        SpawnPlayer(peerId);
    }

    private void OnPeerLeft(long peerId)
    {
        if (!Multiplayer.IsServer())
            return;

        Node? existing = _players.GetNodeOrNull(peerId.ToString());
        existing?.QueueFree();
    }

    private void SpawnPlayer(long peerId)
    {
        if (_players.HasNode(peerId.ToString()))
            return;

        Node3D player = _playerScene.Instantiate<Node3D>();
        // Il nome = id del peer: PlayerController lo usa per impostare l'autorita' di rete,
        // e il MultiplayerSpawner replica nome + istanza ai client.
        player.Name = peerId.ToString();
        player.Position = GetSpawnPoint(_players.GetChildCount());
        _players.AddChild(player, forceReadableName: true);

        GD.Print($"[GameWorld] Avatar spawnato per il peer {peerId}.");
    }

    private static Vector3 GetSpawnPoint(int index)
    {
        // Disposizione semplice in fila, leggermente distanziata; y=1 = centro capsula sul pavimento.
        return new Vector3((index - 1.5f) * 2.0f, 1.0f, 0f);
    }
}
