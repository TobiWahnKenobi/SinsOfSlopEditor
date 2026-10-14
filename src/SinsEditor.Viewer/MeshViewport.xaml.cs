using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SinsEditor.Core.Meshes;
using Media = System.Windows.Media;

namespace SinsEditor.Viewer;

/// <summary>A textured image for a material: PNG (or DDS) bytes, as HelixToolkit loads them.</summary>
public sealed record MeshTexture(byte[] Bytes);

/// <summary>Another mesh shown with the main one (e.g. a particle's mesh on a <c>Flair-</c> hardpoint), placed in its space.</summary>
public sealed record MeshAttachment(MeshData Mesh, Matrix4x4 Placement, IReadOnlyList<MeshTexture?> Textures);

/// <summary>
/// Shows a Sins mesh with its diffuse textures and, optionally, its hardpoints as coloured markers
/// (weapons red, exhausts blue, everything else yellow). Mouse: right-drag rotates, wheel zooms, middle-drag pans.
/// </summary>
public partial class MeshViewport : UserControl
{
    public static readonly DependencyProperty MeshProperty = DependencyProperty.Register(nameof(Mesh), typeof(MeshData), typeof(MeshViewport),
        new PropertyMetadata(null, (d, _) => ((MeshViewport)d).Rebuild()));

    public static readonly DependencyProperty TexturesProperty = DependencyProperty.Register(nameof(Textures), typeof(IReadOnlyList<MeshTexture?>),
        typeof(MeshViewport), new PropertyMetadata(null, (d, _) => ((MeshViewport)d).Rebuild()));

    public static readonly DependencyProperty ShowPointsProperty = DependencyProperty.Register(nameof(ShowPoints), typeof(bool), typeof(MeshViewport),
        new PropertyMetadata(true, (d, _) => ((MeshViewport)d).Rebuild()));

    public static readonly DependencyProperty ShowWireframeProperty = DependencyProperty.Register(nameof(ShowWireframe), typeof(bool), typeof(MeshViewport),
        new PropertyMetadata(false, (d, _) => ((MeshViewport)d).Rebuild()));

    public static readonly DependencyProperty AttachmentsProperty = DependencyProperty.Register(nameof(Attachments), typeof(IReadOnlyList<MeshAttachment>),
        typeof(MeshViewport), new PropertyMetadata(null, (d, _) => ((MeshViewport)d).Rebuild()));

    private static readonly IEffectsManager Effects = new DefaultEffectsManager();

    public MeshViewport()
    {
        InitializeComponent();
        Viewport.EffectsManager = Effects;
    }

    public MeshData? Mesh
    {
        get => (MeshData?)GetValue(MeshProperty);
        set => SetValue(MeshProperty, value);
    }

    /// <summary>Diffuse texture per material, in material order; null where it couldn't be loaded.</summary>
    public IReadOnlyList<MeshTexture?>? Textures
    {
        get => (IReadOnlyList<MeshTexture?>?)GetValue(TexturesProperty);
        set => SetValue(TexturesProperty, value);
    }

    /// <summary>Meshes shown along with <see cref="Mesh"/>, in its coordinate space.</summary>
    public IReadOnlyList<MeshAttachment>? Attachments
    {
        get => (IReadOnlyList<MeshAttachment>?)GetValue(AttachmentsProperty);
        set => SetValue(AttachmentsProperty, value);
    }

    public bool ShowPoints
    {
        get => (bool)GetValue(ShowPointsProperty);
        set => SetValue(ShowPointsProperty, value);
    }

    public bool ShowWireframe
    {
        get => (bool)GetValue(ShowWireframeProperty);
        set => SetValue(ShowWireframeProperty, value);
    }

    public void ZoomExtents() => Viewport.ZoomExtents(300);

    private void Rebuild()
    {
        Models.Children.Clear();
        if (Mesh is not { } mesh || mesh.Vertices.Count == 0) return;
        AddMesh(mesh, Textures, Matrix4x4.Identity);
        foreach (var attachment in Attachments ?? []) AddMesh(attachment.Mesh, attachment.Textures, attachment.Placement);

        AddPoints(mesh);
        if (_framedMesh != mesh) FrameMesh(mesh);
    }

    private void AddMesh(MeshData mesh, IReadOnlyList<MeshTexture?>? textures, Matrix4x4 placement)
    {
        var positions = new Vector3Collection(mesh.Vertices.Select(v => Flip(Vector3.Transform(v.Position, placement))));
        var normals = new Vector3Collection(mesh.Vertices.Select(v => v.Normal == Vector3.Zero ? v.Normal : Flip(Vector3.Normalize(Vector3.TransformNormal(v.Normal, placement)))));
        var uvs = new Vector2Collection(mesh.Vertices.Select(v => v.Uv));

        foreach (var group in mesh.Triangles.GroupBy(t => t.Material).OrderBy(g => g.Key))
        {
            var indices = new IntCollection(group.Count() * 3);
            foreach (var t in group)
            {
                if (t.A >= positions.Count || t.B >= positions.Count || t.C >= positions.Count) continue;
                indices.Add(t.A);
                indices.Add(t.C);
                indices.Add(t.B);
            }
            var geometry = new HelixToolkit.SharpDX.MeshGeometry3D { Positions = positions, Normals = normals, TextureCoordinates = uvs, Indices = indices };
            var material = new PhongMaterial
            {
                DiffuseColor = new Color4(1f, 1f, 1f, 1f),
                AmbientColor = new Color4(0.6f, 0.6f, 0.6f, 1f),
                SpecularColor = new Color4(0.25f, 0.25f, 0.25f, 1f),
                SpecularShininess = 30f,
            };
            if (textures != null && group.Key < textures.Count && textures[group.Key] is { } texture)
                material.DiffuseMap = TextureModel.Create(new System.IO.MemoryStream(texture.Bytes));
            else
                material.DiffuseColor = new Color4(0.62f, 0.64f, 0.7f, 1f);
            Models.Children.Add(new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                CullMode = SharpDX.Direct3D11.CullMode.None,
                RenderWireframe = ShowWireframe,
                WireframeColor = Media.Color.FromRgb(90, 200, 255),
            });
        }
    }

    private void AddPoints(MeshData mesh)
    {
        if (ShowPoints && mesh.Points.Count > 0)
        {
            float size = Math.Max(0.4f, mesh.BoundingRadius / 60f);
            foreach (var kind in mesh.Points.GroupBy(PointKind))
            {
                var builder = new MeshBuilder();
                foreach (var point in kind)
                {
                    var p = Flip(point.Position);
                    builder.AddSphere(p, size, 8, 8);
                    if (point.Forward != Vector3.Zero) builder.AddArrow(p, p + Flip(point.Forward) * size * 5, size * 0.35f, 2, 8);
                }
                var color = kind.Key switch
                {
                    0 => new Color4(1f, 0.25f, 0.2f, 1f),
                    1 => new Color4(0.3f, 0.6f, 1f, 1f),
                    _ => new Color4(1f, 0.85f, 0.2f, 1f),
                };
                Models.Children.Add(new MeshGeometryModel3D
                {
                    Geometry = builder.ToMeshGeometry3D(),
                    Material = new PhongMaterial { DiffuseColor = color, EmissiveColor = color * 0.6f },
                });
            }
        }
    }

    private MeshData? _framedMesh;

    /// <summary>Puts the camera in front, above and to the side of the mesh, far enough to see all of it.</summary>
    private void FrameMesh(MeshData mesh)
    {
        _framedMesh = mesh;
        var center = Flip((mesh.MinExtents + mesh.MaxExtents) / 2);
        float radius = mesh.BoundingRadius > 0 ? mesh.BoundingRadius : Vector3.Distance(mesh.MinExtents, mesh.MaxExtents) / 2;
        // Ships face +Z in the file, which the viewer mirrors to -Z: look from the front, above and to the side.
        var direction = Vector3.Normalize(new Vector3(0.9f, 0.6f, -1.3f));
        float distance = radius / MathF.Tan(45f * MathF.PI / 360f) * 1.15f;
        var position = center + direction * distance;
        Camera.Position = new Media.Media3D.Point3D(position.X, position.Y, position.Z);
        Camera.LookDirection = new Media.Media3D.Vector3D(center.X - position.X, center.Y - position.Y, center.Z - position.Z);
        Camera.UpDirection = new Media.Media3D.Vector3D(0, 1, 0);
    }

    // Sins meshes are left-handed (DirectX); mirror Z (and reverse the winding) for the right-handed viewport.
    private static Vector3 Flip(Vector3 v) => new(v.X, v.Y, -v.Z);

    private static int PointKind(MeshPoint point) =>
        point.Name.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) ? 0
        : point.Name.StartsWith("Exhaust", StringComparison.OrdinalIgnoreCase) ? 1
        : 2;
}
