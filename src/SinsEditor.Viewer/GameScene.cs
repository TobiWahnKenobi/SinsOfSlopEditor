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
    IReadOnlyDictionary<string, Vector4>? Constants = null, Func<float, Matrix4x4>? PlacementAt = null)
{
    /// <summary>Where the item is at a playback time (mesh particles may turn); <see cref="Placement"/> if it doesn't move.</summary>
    public Matrix4x4 PlacementAtTime(float seconds) => PlacementAt?.Invoke(seconds) ?? Placement;
}

/// <summary>Everything the game-shader view draws: the meshes, the skybox's environment maps and the team colour.</summary>
/// <summary>How a particle emitter draws: its effect (fixed function for most) and its two textures.</summary>
public sealed record GameEmitter(EffectFile Effect, PixelImage? Texture0, PixelImage? Texture1);

/// <summary>
/// A simulated particle effect on the mesh (a Flair- hardpoint): its placement, the simulation and per emitter how it draws
/// (null for mesh emitters, which are drawn as attached meshes).
/// </summary>
public sealed record GameParticleEffect(Matrix4x4 Placement, ParticleSystem System, IReadOnlyList<GameEmitter?> Emitters);

/// <remarks>
/// <paramref name="Skybox"/> is the skybox backdrop mesh, drawn first around the camera without depth.
/// <paramref name="Background"/> is a fixed 2D picture behind everything (instead of the skybox), filling the view.
/// <paramref name="StarDistance"/> is how far the light (the star) is, in game units: the base game's shaders only use its
/// direction, but PBR shaders brighten the light near a star (SinsRemastered: radius 25,000, up to 8× intensity), so a light
/// placed close to the model makes everything white.
/// </remarks>
public sealed record GameScene(MeshData Primary, IReadOnlyList<GameSceneItem> Items, CubeMap? Environment, CubeMap? Irradiance, Vector4 TeamColor,
    GameSceneItem? Skybox = null, float StarDistance = 100_000f, IReadOnlyList<GameParticleEffect>? Particles = null,
    PixelImage? Background = null)
{
    /// <summary>Whether playback changes anything: particles, or meshes that move over time.</summary>
    public bool IsAnimated => Particles is { Count: > 0 } || Items.Any(i => i.PlacementAt != null);
}

/// <summary>
/// Where the camera looks from: an orbit around <see cref="Target"/> in game space (left-handed, ships face +Z), with a
/// vertical field of view in degrees. Up is always +Y, so the pitch stops just short of straight up or down.
/// </summary>
public record struct OrbitCamera(Vector3 Target, float Yaw, float Pitch, float Distance, float FieldOfView = 45f)
{
    public const float MaxPitch = 1.5699f;
    public const float MinFieldOfView = 5f, MaxFieldOfView = 120f;

    public readonly Vector3 Direction => new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
    public readonly Vector3 Eye => Target + Direction * Distance;
    public readonly float FieldOfViewRadians => Math.Clamp(FieldOfView, MinFieldOfView, MaxFieldOfView) * MathF.PI / 180;

    /// <summary>In front of the ship, above and to its left, far enough to see all of it: the bow points to the bottom left.</summary>
    public static OrbitCamera Frame(MeshData mesh, float fieldOfView = 45f) =>
        Looking(mesh, new Vector3(-0.9f, 0.6f, 1.3f), fieldOfView);

    /// <summary>From straight above, the bow pointing up.</summary>
    public static OrbitCamera TopDown(MeshData mesh, float fieldOfView = 45f) => Looking(mesh, new Vector3(0, 1, -0.0001f), fieldOfView);

    /// <summary>Looking at the bow.</summary>
    public static OrbitCamera Front(MeshData mesh, float fieldOfView = 45f) => Looking(mesh, Vector3.UnitZ, fieldOfView);

    /// <summary>Looking at the ship's left side (from -X; ships face +Z with +X to their right).</summary>
    public static OrbitCamera Side(MeshData mesh, float fieldOfView = 45f) => Looking(mesh, -Vector3.UnitX, fieldOfView);

    public static OrbitCamera Back(MeshData mesh, float fieldOfView = 45f) => Looking(mesh, -Vector3.UnitZ, fieldOfView);

    /// <summary>At the mesh's centre from <paramref name="direction"/>, far enough to see all of it.</summary>
    public static OrbitCamera Looking(MeshData mesh, Vector3 direction, float fieldOfView = 45f)
    {
        var center = (mesh.MinExtents + mesh.MaxExtents) / 2;
        float radius = mesh.BoundingRadius > 0 ? mesh.BoundingRadius : Vector3.Distance(mesh.MinExtents, mesh.MaxExtents) / 2;
        var camera = new OrbitCamera(center, 0, 0, 1, fieldOfView).Toward(direction);
        return camera with { Distance = Math.Max(1f, radius / MathF.Tan(camera.FieldOfViewRadians / 2) * 1.15f) };
    }

    /// <summary>The same orbit seen from <paramref name="direction"/> (from the target towards the eye).</summary>
    public readonly OrbitCamera Toward(Vector3 direction)
    {
        if (direction.LengthSquared() < 1e-12f) return this;
        direction = Vector3.Normalize(direction);
        float pitch = Math.Clamp(MathF.Asin(Math.Clamp(direction.Y, -1, 1)), -MaxPitch, MaxPitch);
        // Straight up or down has no yaw of its own: keep the current one.
        float yaw = MathF.Abs(direction.X) + MathF.Abs(direction.Z) < 1e-6f ? Yaw : MathF.Atan2(direction.X, direction.Z);
        return this with { Yaw = yaw, Pitch = pitch };
    }

    /// <summary>The eye turned by <paramref name="degrees"/> around a world axis through the target.</summary>
    public readonly OrbitCamera Rotated(Vector3 axis, float degrees) =>
        Toward(Vector3.Transform(Direction, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180)));

    /// <summary>Camera and target moved together by <paramref name="offset"/> game units.</summary>
    public readonly OrbitCamera Moved(Vector3 offset) => this with { Target = Target + offset };
}
