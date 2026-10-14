using System.Numerics;

namespace SinsEditor.Core.Meshes;

/// <summary>An emitter of a particle effect: its mesh (for mesh particles), where it sits in the effect, and its shader.</summary>
public sealed record ParticleEmitter(string Name, string MeshName, Vector3 Position, Matrix4x4 Orientation, string Effect = "",
    float AngularSpeed = 0, Vector3 RotationAxis = default)
{
    /// <summary>How a mesh particle turns over time: around its rotation axis (emitter space) at its angular speed (radians/second).</summary>
    public Matrix4x4 SpinAt(float seconds) =>
        AngularSpeed == 0 || RotationAxis.LengthSquared() < 1e-8f ? Matrix4x4.Identity
            : Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(RotationAxis), AngularSpeed * seconds);
}

/// <summary>The emitters of a Sins <c>.particle</c> in its text form; enough to place the meshes it shows.</summary>
public sealed class ParticleData
{
    public List<ParticleEmitter> Emitters { get; } = [];

    public static ParticleData Load(string textPath) => Parse(File.ReadLines(textPath));

    public static ParticleData Parse(IEnumerable<string> lines)
    {
        var data = new ParticleData();
        bool inEmitter = false, positioned = false, oriented = false;
        string name = "", mesh = "", effect = "";
        float minSpin = 0, maxSpin = 0;
        bool rotates = true;
        Vector3 axis = default;
        Vector3 position = default;
        var orientation = Matrix4x4.Identity;
        int orientationRow = -1;

        void Flush()
        {
            if (inEmitter) data.Emitters.Add(new ParticleEmitter(name, mesh, position, orientation, effect, rotates ? (minSpin + maxSpin) / 2 : 0, axis));
            minSpin = maxSpin = 0;
            rotates = true;
            axis = default;
            inEmitter = positioned = oriented = false;
            name = mesh = effect = "";
            position = default;
            orientation = Matrix4x4.Identity;
            orientationRow = -1;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                if (orientationRow >= 0) orientation = MeshMath.WithRow(orientation, orientationRow, MeshData.Vector(line));
                if (orientationRow >= 0 && ++orientationRow == 3) orientationRow = -1;
                continue;
            }
            int space = line.IndexOf(' ');
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (key)
            {
                case "EmitterContents": Flush(); inEmitter = true; break;
                // Affectors follow the emitters; their keys aren't the emitter's.
                case "NumAffectors": Flush(); break;
                case "Name" when inEmitter && name.Length == 0: name = MeshData.Unquote(value); break;
                case "MeshName" when inEmitter: mesh = MeshData.Unquote(value); break;
                case "PipelineEffectID" when inEmitter: effect = MeshData.Unquote(value); break;
                case "ParticleMinStartAngularSpeed" when inEmitter: minSpin = MeshData.Float(value); break;
                case "ParticleMaxStartAngularSpeed" when inEmitter: maxSpin = MeshData.Float(value); break;
                case "ParticlesRotate" when inEmitter: rotates = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase); break;
                case "MeshParticleRotationAxis" when inEmitter: axis = MeshData.Vector(value); break;
                // The emitter's own placement comes first; later Position keys belong to nested settings.
                case "Position" when inEmitter && !positioned: position = MeshData.Vector(value); positioned = true; break;
                case "Orientation" when inEmitter && positioned && !oriented: orientationRow = 0; oriented = true; break;
            }
        }
        Flush();
        return data;
    }
}

public static class MeshMath
{
    /// <summary>Sets row <paramref name="row"/> (0–2) of the rotation part.</summary>
    public static Matrix4x4 WithRow(Matrix4x4 m, int row, Vector3 v)
    {
        switch (row)
        {
            case 0: m.M11 = v.X; m.M12 = v.Y; m.M13 = v.Z; break;
            case 1: m.M21 = v.X; m.M22 = v.Y; m.M23 = v.Z; break;
            case 2: m.M31 = v.X; m.M32 = v.Y; m.M33 = v.Z; break;
        }
        return m;
    }

    /// <summary>A placement: the rotation rows as the local axes (DirectX row-vector convention) plus a translation.</summary>
    public static Matrix4x4 Placement(Matrix4x4 orientation, Vector3 position)
    {
        var m = orientation;
        m.M41 = position.X; m.M42 = position.Y; m.M43 = position.Z; m.M44 = 1;
        return m;
    }
}
