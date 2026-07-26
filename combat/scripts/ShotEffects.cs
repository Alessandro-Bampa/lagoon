using Godot;

namespace Lagoon;

/// <summary>
/// Effetti visivi del tiro: traccianti, vampa alla bocca, impatti. Placeholder geometrici
/// (CLAUDE.md §7), nessun asset.
///
/// Tutto qui dentro e' PURA ESTETICA (CLAUDE.md §3.3): questi nodi vengono creati in locale su ogni
/// peer che riceve <see cref="WeaponController.BroadcastShot"/>. Non passano da un
/// <c>MultiplayerSpawner</c> e non hanno alcuno stato replicato — replicare coriandoli sarebbe
/// banda sprecata, e la loro assenza non puo' desincronizzare la partita.
/// </summary>
public static class ShotEffects
{
    private const float TracerSeconds = 0.06f;
    private const float FlashSeconds = 0.05f;
    private const float ImpactSeconds = 0.15f;

    private static readonly Color TracerColor = new(1f, 0.85f, 0.35f);
    private static readonly Color ImpactColor = new(1f, 0.4f, 0.2f);

    /// Segmento luminoso fra la bocca dell'arma e il punto d'arrivo del colpo.
    public static void SpawnTracer(Node parent, Vector3 from, Vector3 to)
    {
        float length = from.DistanceTo(to);
        if (length < 0.05f)
            return;

        var tracer = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.02f, length) },
            MaterialOverride = Unshaded(TracerColor),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        parent.AddChild(tracer);
        Vector3 center = (from + to) * 0.5f;
        // LookAt fallirebbe se il segmento fosse verticale puro: gestito dal length check + up scelto.
        Vector3 up = Mathf.Abs((to - from).Normalized().Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
        tracer.GlobalPosition = center;
        tracer.LookAt(to, up);

        FreeAfter(tracer, TracerSeconds);
    }

    /// Lampo alla bocca dell'arma, sul supporto che la regge.
    public static void SpawnMuzzleFlash(Node3D mount)
    {
        var flash = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f, RadialSegments = 6, Rings = 3 },
            MaterialOverride = Unshaded(TracerColor),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0f, 0f, 0.5f),
        };
        mount.AddChild(flash);

        var light = new OmniLight3D
        {
            LightColor = TracerColor,
            LightEnergy = 2.5f,
            OmniRange = 3f,
            Position = flash.Position,
        };
        mount.AddChild(light);

        FreeAfter(flash, FlashSeconds);
        FreeAfter(light, FlashSeconds);
    }

    /// Puntino sul punto d'impatto.
    public static void SpawnImpact(Node parent, Vector3 position)
    {
        var impact = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 6, Rings = 3 },
            MaterialOverride = Unshaded(ImpactColor),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(impact);
        impact.GlobalPosition = position;

        FreeAfter(impact, ImpactSeconds);
    }

    private static StandardMaterial3D Unshaded(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        EmissionEnabled = true,
        Emission = color,
        DisableReceiveShadows = true,
    };

    /// Auto-distruzione dopo <paramref name="seconds"/>. Il timer e' figlio del nodo stesso, cosi'
    /// se la scena viene scaricata prima sparisce con lui.
    private static void FreeAfter(Node node, float seconds)
    {
        var timer = new Timer { WaitTime = seconds, OneShot = true, Autostart = true };
        node.AddChild(timer);
        timer.Timeout += node.QueueFree;
    }
}
