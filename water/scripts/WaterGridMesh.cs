using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Griglia dell'acqua: un'unica mesh quadrata, fitta al centro e sempre piu' rada verso il bordo
/// (passo che cresce in progressione geometrica), che <see cref="WaterSurface"/> tiene centrata sulla
/// camera. E' la soluzione "a griglia stirata": nessuna giunzione fra anelli di LOD (niente crepe) e
/// un costo fisso, indipendente dalla dimensione della mappa.
///
/// <para>Ogni vertice porta in <c>UV2.x</c> il proprio passo di griglia: lo shader spegne le onde
/// troppo corte per quel passo (lunghezza d'onda &lt; 2-4 passi), che altrimenti si vedrebbero
/// "nuotare" quando la griglia si sposta col giocatore. Le onde spente in geometria restano nelle
/// normali di dettaglio.</para>
/// </summary>
public static class WaterGridMesh
{
    public readonly record struct Layout(float InnerSpacing, float InnerHalfSize, float Growth, float Extent);

    /// Coordinate lungo un asse, simmetriche attorno a 0.
    public static float[] AxisCoordinates(in Layout layout, out float[] spacing)
    {
        var half = new List<float> { 0f };
        var halfSpacing = new List<float> { layout.InnerSpacing };
        float pos = 0f;
        float step = layout.InnerSpacing;
        while (pos < layout.Extent)
        {
            if (pos >= layout.InnerHalfSize)
                step *= layout.Growth;
            pos += step;
            half.Add(pos);
            halfSpacing.Add(step);
        }

        int n = half.Count;
        var coords = new float[2 * n - 1];
        spacing = new float[2 * n - 1];
        for (int i = 0; i < n; i++)
        {
            coords[n - 1 + i] = half[i];
            coords[n - 1 - i] = -half[i];
            spacing[n - 1 + i] = halfSpacing[i];
            spacing[n - 1 - i] = halfSpacing[i];
        }
        return coords;
    }

    public static ArrayMesh Build(in Layout layout)
    {
        float[] axis = AxisCoordinates(layout, out float[] spacing);
        int n = axis.Length;

        var vertices = new Vector3[n * n];
        var uv2 = new Vector2[n * n];
        var normals = new Vector3[n * n];
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                vertices[i] = new Vector3(axis[x], 0f, axis[z]);
                uv2[i] = new Vector2(Mathf.Max(spacing[x], spacing[z]), 0f);
                normals[i] = Vector3.Up;
            }
        }

        var indices = new int[(n - 1) * (n - 1) * 6];
        int k = 0;
        for (int z = 0; z < n - 1; z++)
        {
            for (int x = 0; x < n - 1; x++)
            {
                int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                // Senso orario visto dall'alto = faccia frontale verso +Y in Godot.
                indices[k++] = a; indices[k++] = b; indices[k++] = c;
                indices[k++] = b; indices[k++] = d; indices[k++] = c;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // Le onde spostano i vertici nello shader: l'AABB deve contenerle, o la mesh sparisce ai bordi.
        float e = layout.Extent + 10f;
        mesh.CustomAabb = new Aabb(new Vector3(-e, -30f, -e), new Vector3(2f * e, 60f, 2f * e));
        GD.Print($"[WaterGridMesh] {n}x{n} vertici ({n * n / 1000}k), {indices.Length / 3 / 1000}k triangoli.");
        return mesh;
    }
}
