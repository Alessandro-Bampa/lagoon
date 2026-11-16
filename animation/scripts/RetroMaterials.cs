using Godot;

namespace Lagoon;

/// <summary>
/// Impostazioni dei materiali del Retro Weapon Pack, come da suo Readme: texture senza filtro
/// (pixel netti), roughness 1, specular 0. Si applicano all'istanza invece che nell'import
/// perche' i <c>.glb</c> si rigenerano e il materiale importato tornerebbe ai default.
/// </summary>
public static class RetroMaterials
{
    public static void Apply(Node root)
    {
        foreach (Node node in root.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            var instance = (MeshInstance3D)node;
            if (instance.Mesh == null)
                continue;

            for (int surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                // Il materiale e' una risorsa condivisa dall'import: modificarlo una volta vale
                // per tutte le istanze, e ripeterlo e' innocuo.
                if (instance.Mesh.SurfaceGetMaterial(surface) is not BaseMaterial3D material)
                    continue;

                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
                material.Roughness = 1.0f;
                material.MetallicSpecular = 0.0f;
            }
        }
    }
}
