using System.Numerics;
using SinsEditor.Core.Meshes;
using SinsEditor.Core.Textures;

namespace SinsEditor.Viewer;

/// <summary>
/// The textures of one mesh material as the game shaders read them: diffuse (-cl, alpha = team colour mask),
/// data (-da: R specular, G self-illumination, B environment), normal map (-nm, DXT5_NM layout) and team colour, plus the
/// material's colours from the mesh file (g_MaterialDiffuse … ; a mod's planet shader picks its pass from the Specular alpha).
/// </summary>
public sealed record GameMaterial(PixelImage? Diffuse, PixelImage? Data, PixelImage? Normal, PixelImage? TeamColor, float Glossiness,
    Vector4 DiffuseColor, Vector4 AmbientColor, Vector4 SpecularColor, Vector4 EmissiveColor, PixelImage? Displacement = null)
{
    public static GameMaterial From(MeshMaterial m, Func<string, PixelImage?> image) => new(
        image(m.Diffuse), image(m.SelfIllumination), image(m.Normal), image(m.TeamColor), m.Glossiness,
        MeshMaterial.ToVector(m.DiffuseColor), MeshMaterial.ToVector(m.AmbientColor), MeshMaterial.ToVector(m.SpecularColor), MeshMaterial.ToVector(m.EmissiveColor),
        image(m.Displacement));
}

/// <summary>
/// A mesh drawn with a game effect at a placement (the main mesh at identity, particle meshes on their hardpoints).
/// <paramref name="Passes"/> names the passes to draw (null: all, as the engine does; e.g. without a planet's cloud layer).
/// <paramref name="Textures"/> supplies effect textures that don't come from the mesh, e.g. a planet's cloud layer (g_TextureDiffuse2);
/// <paramref name="Constants"/> likewise for values, e.g. a planet's g_CloudColor and g_GlowColor.
/// </summary>
public sealed record GameSceneItem(MeshData Mesh, Matrix4x4 Placement, IReadOnlyList<GameMaterial> Materials, EffectFile Effect,
    IReadOnlyCollection<string>? Passes = null, IReadOnlyDictionary<string, PixelImage>? Textures = null,
    IReadOnlyDictionary<string, Vector4>? Constants = null);

/// <summary>Everything the game-shader view draws: the meshes, the skybox's environment maps and the team colour.</summary>
/// <remarks><paramref name="Skybox"/> is the skybox backdrop mesh, drawn first around the camera without depth.</remarks>
public sealed record GameScene(MeshData Primary, IReadOnlyList<GameSceneItem> Items, CubeMap? Environment, CubeMap? Irradiance, Vector4 TeamColor,
    GameSceneItem? Skybox = null);

/// <summary>Where the camera looks from: an orbit around <see cref="Target"/> in game space (left-handed, ships face +Z).</summary>
public record struct OrbitCamera(Vector3 Target, float Yaw, float Pitch, float Distance)
{
    public readonly Vector3 Direction => new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
    public readonly Vector3 Eye => Target + Direction * Distance;

    /// <summary>In front of the ship, above and to its left, far enough to see all of it: the bow points to the bottom left.</summary>
    public static OrbitCamera Frame(MeshData mesh)
    {
        var center = (mesh.MinExtents + mesh.MaxExtents) / 2;
        float radius = mesh.BoundingRadius > 0 ? mesh.BoundingRadius : Vector3.Distance(mesh.MinExtents, mesh.MaxExtents) / 2;
        var dir = Vector3.Normalize(new Vector3(-0.9f, 0.6f, 1.3f));
        return new OrbitCamera(center, MathF.Atan2(dir.X, dir.Z), MathF.Asin(dir.Y), Math.Max(1f, radius / MathF.Tan(45f * MathF.PI / 360f) * 1.15f));
    }
}
