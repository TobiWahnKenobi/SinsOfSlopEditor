using System.Globalization;
using System.Numerics;

namespace SinsEditor.Core.Meshes;

public sealed record MeshMaterial(string Diffuse, string SelfIllumination, string Displacement, string TeamColor);

/// <summary>
/// A hardpoint: weapons, exhausts, ability and effect attachment points (<c>Weapon-0</c>, <c>Exhaust</c>, <c>Center</c> …).
/// <paramref name="Orientation"/> holds the three rows of the file's orientation matrix as its first three rows
/// (row 1 is the forward direction); <c>Flair-&lt;particle&gt;</c> points attach a particle with it.
/// </summary>
public sealed record MeshPoint(string Name, Vector3 Position, Matrix4x4 Orientation)
{
    public Vector3 Forward => new(Orientation.M11, Orientation.M12, Orientation.M13);

    /// <summary>The particle a <c>Flair-</c> point attaches, if it is one.</summary>
    public string? FlairParticle => Name.StartsWith("Flair-", StringComparison.OrdinalIgnoreCase) && Name.Length > 6 ? Name[6..] : null;
}

public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

public readonly record struct MeshTriangle(int A, int B, int C, int Material);

/// <summary>
/// The geometry of a Sins <c>.mesh</c> in its text form (<c>MeshData</c> with materials, points, vertices and triangles).
/// Read straight from the lines, since meshes have tens of thousands of them.
/// </summary>
public sealed class MeshData
{
    public List<MeshMaterial> Materials { get; } = [];
    public List<MeshPoint> Points { get; } = [];
    public List<MeshVertex> Vertices { get; } = [];
    public List<MeshTriangle> Triangles { get; } = [];
    public float BoundingRadius { get; private set; }
    public Vector3 MinExtents { get; private set; }
    public Vector3 MaxExtents { get; private set; }

    public static MeshData Load(string textPath) => Parse(File.ReadLines(textPath));

    public static MeshData Parse(IEnumerable<string> lines)
    {
        var mesh = new MeshData();
        string? diffuse = null, self = null, displacement = null, team = null;
        string? pointName = null;
        Vector3 pointPosition = default;
        int orientationRow = -1;
        var orientation = Matrix4x4.Identity;
        Vector3 position = default, normal = default;
        float u = 0, v = 0;
        bool inVertex = false;
        int a = 0, b = 0, c = 0;
        string section = "";

        void FlushMaterial()
        {
            if (diffuse is null && self is null && displacement is null && team is null) return;
            mesh.Materials.Add(new MeshMaterial(diffuse ?? "", self ?? "", displacement ?? "", team ?? ""));
            diffuse = self = displacement = team = null;
        }
        void FlushPoint()
        {
            if (pointName is null) return;
            mesh.Points.Add(new MeshPoint(pointName, pointPosition, orientation));
            pointName = null;
            pointPosition = default;
            orientation = Matrix4x4.Identity;
        }
        void FlushVertex()
        {
            if (!inVertex) return;
            mesh.Vertices.Add(new MeshVertex(position, normal, new Vector2(u, v)));
            inVertex = false;
            u = v = 0;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            // Orientation rows: three vectors on their own lines.
            if (line[0] == '[')
            {
                if (orientationRow >= 0) orientation = MeshMath.WithRow(orientation, orientationRow, Vector(line));
                if (orientationRow >= 0 && ++orientationRow == 3) orientationRow = -1;
                continue;
            }
            int space = line.IndexOf(' ');
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (key)
            {
                case "Material": FlushMaterial(); section = key; break;
                case "Point": FlushPoint(); section = key; pointName = ""; break;
                case "Vertex": FlushPoint(); FlushVertex(); section = key; inVertex = true; break;
                case "Triangle": FlushVertex(); section = key; break;
                case "DiffuseTextureFileName": diffuse = Unquote(value); break;
                case "SelfIlluminationTextureFileName": self = Unquote(value); break;
                case "DisplacementTextureFileName": displacement = Unquote(value); break;
                case "TeamColorTextureFileName": team = Unquote(value); break;
                case "BoundingRadius": mesh.BoundingRadius = Float(value); break;
                case "MaxBoundingExtents": mesh.MaxExtents = Vector(value); break;
                case "MinBoundingExtents": mesh.MinExtents = Vector(value); break;
                case "DataString" when section == "Point": pointName = Unquote(value); break;
                case "Position" when section == "Point": pointPosition = Vector(value); break;
                case "Orientation" when section == "Point": orientationRow = 0; break;
                case "Position": position = Vector(value); break;
                case "Normal": normal = Vector(value); break;
                case "U0": u = Float(value); break;
                case "V0": v = Float(value); break;
                case "iVertex0": a = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "iVertex1": b = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "iVertex2": c = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "iMaterial": mesh.Triangles.Add(new MeshTriangle(a, b, c, int.Parse(value, CultureInfo.InvariantCulture))); break;
                case "NumPoints": FlushMaterial(); break;
                case "NumVertices": FlushMaterial(); FlushPoint(); break;
                case "NumTriangles": FlushVertex(); break;
            }
        }
        FlushMaterial();
        FlushPoint();
        FlushVertex();
        return mesh;
    }

    internal static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    internal static float Float(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    internal static Vector3 Vector(string value)
    {
        var parts = value.Trim('[', ']', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? new Vector3(Float(parts[0]), Float(parts[1]), Float(parts[2])) : default;
    }
}
