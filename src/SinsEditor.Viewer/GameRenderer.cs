using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using SinsEditor.Core.Meshes;
using SinsEditor.Core.Settings;
using SinsEditor.Core.Textures;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace SinsEditor.Viewer;

/// <summary>Display options of the game-shader view.</summary>
/// <summary>
/// Display options of the game-shader view. <paramref name="Transparent"/> renders without skybox or background and gives
/// the picture an alpha channel (mesh coverage, particle brightness), for exporting images. <paramref name="LightDirection"/>
/// points from the model to the sun (null: <see cref="DefaultLightDirection"/>). <paramref name="Cel"/> draws the meshes in the
/// editor's cel style instead of with the game's shaders; <paramref name="LineScale"/> scales its line width (supersampled pictures).
/// </summary>
public sealed record GameRenderOptions(bool ShowPoints, bool Wireframe, bool ShowParticles = true, bool Transparent = false,
    PixelImage? Background = null, float Time = 0, Vector3? LightDirection = null, CelStyle? Cel = null, float LineScale = 1)
{
    /// <summary>Above, in front and to the left of the ship.</summary>
    public static Vector3 DefaultLightDirection { get; } = Vector3.Normalize(new Vector3(-0.6f, 0.9f, 0.8f));
}

/// <summary>
/// Draws a <see cref="GameScene"/> with the game's own shaders on Direct3D 11, offscreen, and returns the picture as BGRA
/// pixels (WPF shows them). The engine's inputs are recreated: camera-relative world space (the shaders take the camera
/// at the origin), one light, the skybox's environment maps, the team colour, and a shadow map rendered from the light
/// for the shadowed shaders.
/// </summary>
public sealed class GameRenderer : IDisposable
{
    private const int ShadowSize = 2048;
    private const int Msaa = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position, Normal, Tangent, Binormal;
        public Vector2 Uv0, Uv1;
        public uint Color;
    }

    private static readonly InputElement[] Layout =
    [
        new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        new("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
        new("BINORMAL", 0, Format.R32G32B32_Float, 36, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, 48, 0),
        new("TEXCOORD", 1, Format.R32G32_Float, 56, 0),
        new("COLOR", 0, Format.R8G8B8A8_UNorm, 64, 0),
    ];
    private static readonly int Stride = Marshal.SizeOf<Vertex>();

    private const string HelperShaders = """
        cbuffer C : register(b0) { row_major float4x4 WVP; float4 Color; };
        struct VI { float3 p : POSITION; float3 n : NORMAL; };
        struct VO { float4 p : SV_Position; float3 n : NORMAL; float d : DEPTH; };
        VO VS(VI i) { VO o; o.p = mul(float4(i.p, 1), WVP); o.n = i.n; o.d = o.p.z / o.p.w; return o; }
        float4 Flat(VO i) : SV_Target { return Color * (0.65 + 0.35 * saturate(dot(normalize(i.n), normalize(float3(-0.4, 0.8, 0.5))))); }
        float4 Wire(VO i) : SV_Target { return Color; }
        float Depth(VO i) : SV_Target { return i.d; }
        Texture2D BackgroundTexture : register(t0);
        SamplerState BackgroundSampler : register(s0);
        struct BO { float4 p : SV_Position; float2 uv : TEXCOORD0; };
        BO BackgroundVS(uint id : SV_VertexID)
        {
            BO o;
            float2 t = float2((id << 1) & 2, id & 2);
            o.p = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = t * Color.xy + Color.zw;
            return o;
        }
        float4 BackgroundPS(BO i) : SV_Target { return BackgroundTexture.Sample(BackgroundSampler, i.uv); }
        """;

    private sealed class StageInfo
    {
        public required int CbSize;
        public required List<(string Name, int Offset, int Size, byte[]? Default)> Variables;
        public required List<(string Name, int Slot, ShaderInputType Type, ShaderResourceViewDimension Dimension)> Bindings;
    }

    private sealed class GpuPass : IDisposable
    {
        public required string Name;
        public required VertexShader Vs;
        public required PixelShader Ps;
        public required InputLayout InputLayout;
        public required StageInfo VsInfo, PsInfo;
        public required Buffer VsCb, PsCb;
        public required BlendState Blend;
        public required DepthStencilState Depth;
        public required RasterizerState Raster;
        public void Dispose()
        {
            Vs.Dispose(); Ps.Dispose(); InputLayout.Dispose(); VsCb.Dispose(); PsCb.Dispose(); Blend.Dispose(); Depth.Dispose(); Raster.Dispose();
        }
    }

    private sealed class GpuItem : IDisposable
    {
        public required GameSceneItem Item;
        public required Buffer Vertices;
        public required List<(Buffer Indices, int Count, int Material)> Groups;
        public required IReadOnlyList<GpuPass> Passes;
        public required IReadOnlyDictionary<string, EffectSampler> Samplers;
        public void Dispose()
        {
            Vertices.Dispose();
            foreach (var g in Groups) g.Indices.Dispose();
        }
    }

    private readonly Device _device;
    private readonly DeviceContext _context;
    private readonly Dictionary<string, IReadOnlyList<GpuPass>> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PixelImage, ShaderResourceView> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CubeMap, ShaderResourceView> _cubes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(bool Clamp, bool Point), SamplerState> _samplers = [];
    private readonly List<GpuItem> _items = [];
    private GpuItem? _skybox;
    private readonly List<(Buffer Vertices, Buffer Indices, int Count, Vector4 Color)> _markers = [];
    private readonly ShaderResourceView _grey, _flatNormal, _black, _white, _greyCube, _noise3D;
    private readonly VertexShader _helperVs;
    private readonly PixelShader _flatPs, _wirePs, _depthPs;
    private readonly InputLayout _helperLayout;
    private readonly Buffer _helperCb;
    private readonly RasterizerState _solid, _wireRaster, _noCull;
    private readonly DepthStencilState _depthOn, _depthRead, _noDepth;
    private readonly BlendState _opaque;
    private GameScene? _scene;

    // Targets, recreated on resize.
    private int _width, _height;
    private Texture2D? _color, _resolved, _staging, _depthBuffer, _mask, _maskResolved, _maskStaging;
    private RenderTargetView? _colorView, _maskView;
    private VertexShader? _backgroundVs;
    private PixelShader? _backgroundPs;
    private DepthStencilView? _depthView;
    // Normals and depth of the cel style, single-sampled.
    private Texture2D? _geometry, _geometryDepth;
    private RenderTargetView? _geometryView;
    private ShaderResourceView? _geometryResource;
    private DepthStencilView? _geometryDepthView;

    // Shadow map from the light.
    private readonly Texture2D _shadow, _shadowDepth;
    private readonly RenderTargetView _shadowView;
    private readonly DepthStencilView _shadowDepthView;
    private readonly ShaderResourceView _shadowResource;

    public string? Error { get; private set; }

    public GameRenderer()
    {
        try
        {
            _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        catch (SharpDX.SharpDXException)
        {
            _device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        }
        _context = _device.ImmediateContext;

        _grey = Solid(128, 128, 128, 0);
        _flatNormal = Solid(128, 128, 128, 128);
        _black = Solid(0, 0, 0, 0);
        _white = Solid(255, 255, 255, 255);
        _greyCube = CubeView(new CubeMap(1, Enumerable.Range(0, 6).Select(_ => new PixelImage(1, 1, [70, 66, 64, 255])).ToList()));
        _noise3D = Noise3D(64);

        byte[] vs = CompileHelper("VS", "vs_4_0");
        _helperVs = new VertexShader(_device, vs);
        _helperLayout = new InputLayout(_device, vs, Layout);
        _flatPs = new PixelShader(_device, CompileHelper("Flat", "ps_4_0"));
        _wirePs = new PixelShader(_device, CompileHelper("Wire", "ps_4_0"));
        _depthPs = new PixelShader(_device, CompileHelper("Depth", "ps_4_0"));
        _backgroundVs = new VertexShader(_device, CompileHelper("BackgroundVS", "vs_4_0"));
        _backgroundPs = new PixelShader(_device, CompileHelper("BackgroundPS", "ps_4_0"));
        _helperCb = new Buffer(_device, 80, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _solid = Raster(CullMode.Back, FillMode.Solid);
        _noCull = Raster(CullMode.None, FillMode.Solid);
        _wireRaster = new RasterizerState(_device, new RasterizerStateDescription
        {
            CullMode = CullMode.None, FillMode = FillMode.Wireframe, IsDepthClipEnabled = true, IsAntialiasedLineEnabled = true,
            DepthBias = -1000, SlopeScaledDepthBias = -1f,
        });
        _depthOn = new DepthStencilState(_device, new DepthStencilStateDescription
            { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.LessEqual });
        _depthRead = new DepthStencilState(_device, new DepthStencilStateDescription
            { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual });
        _opaque = new BlendState(_device, BlendStateDescription.Default());
        _noDepth = new DepthStencilState(_device, new DepthStencilStateDescription { IsDepthEnabled = false, DepthWriteMask = DepthWriteMask.Zero });

        _shadow = new Texture2D(_device, new Texture2DDescription
        {
            Width = ShadowSize, Height = ShadowSize, MipLevels = 1, ArraySize = 1, Format = Format.R32_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _shadowView = new RenderTargetView(_device, _shadow);
        _shadowResource = new ShaderResourceView(_device, _shadow);
        _shadowDepth = new Texture2D(_device, new Texture2DDescription
        {
            Width = ShadowSize, Height = ShadowSize, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
        });
        _shadowDepthView = new DepthStencilView(_device, _shadowDepth);
    }

    private static byte[] CompileHelper(string entry, string profile)
    {
        using var result = ShaderBytecode.Compile(HelperShaders, entry, profile, ShaderFlags.OptimizationLevel3);
        return result.Bytecode.Data;
    }

    private RasterizerState Raster(CullMode cull, FillMode fill) => new(_device, new RasterizerStateDescription
        { CullMode = cull, FillMode = fill, IsDepthClipEnabled = true, IsMultisampleEnabled = true });

    // ---- Scene ----

    /// <summary>Compiles the scene's effects and uploads its meshes and textures. Errors end up in <see cref="Error"/>; items whose effect fails are skipped.</summary>
    public void SetScene(GameScene scene)
    {
        foreach (var item in _items) item.Dispose();
        _items.Clear();
        _skybox?.Dispose();
        _skybox = null;
        foreach (var m in _markers) { m.Vertices.Dispose(); m.Indices.Dispose(); }
        _markers.Clear();
        _scene = scene;
        var errors = new List<string>();

        foreach (var item in scene.Items)
        {
            IReadOnlyList<GpuPass> passes;
            try
            {
                passes = EffectPasses(item.Effect);
            }
            catch (Exception e) when (e is ShaderCompileException or SharpDX.SharpDXException)
            {
                if (!errors.Contains(e.Message)) errors.Add(e.Message);
                continue;
            }
            _items.Add(Upload(item, passes));
        }
        if (scene.Skybox is { } sky)
        {
            try
            {
                _skybox = Upload(sky, EffectPasses(sky.Effect));
            }
            catch (Exception e) when (e is ShaderCompileException or SharpDX.SharpDXException)
            {
                errors.Add(e.Message);
            }
        }
        BuildMarkers(scene.Primary);
        Error = errors.Count == 0 ? null : string.Join("\n", errors);
    }

    private IReadOnlyList<GpuPass> EffectPasses(EffectFile effect)
    {
        if (_effects.TryGetValue(effect.Name, out var cached)) return cached;
        var passes = GameShaderCompiler.Compile(effect).Select(CreatePass).ToList();
        _effects[effect.Name] = passes;
        return passes;
    }

    private GpuPass CreatePass(CompiledPass pass)
    {
        var states = pass.Pass.States;
        string State(string key, string fallback) => states.TryGetValue(key, out var v) ? v.Trim().ToUpperInvariant() : fallback;

        var blend = BlendStateDescription.Default();
        if (State("AlphaBlendEnable", "FALSE") == "TRUE")
        {
            var target = blend.RenderTarget[0];
            target.IsBlendEnabled = true;
            target.SourceBlend = target.SourceAlphaBlend = BlendFactor(State("SrcBlend", "ONE"));
            target.DestinationBlend = target.DestinationAlphaBlend = BlendFactor(State("DestBlend", "ZERO"));
            target.BlendOperation = target.AlphaBlendOperation = BlendOperation.Add;
            blend.RenderTarget[0] = target;
        }
        bool depthTest = State("ZEnable", "TRUE") != "FALSE";
        bool depthWrite = State("ZWriteEnable", "TRUE") != "FALSE";
        // Direct3D 9 culls counter-clockwise faces by default, as Direct3D 11 does with clockwise fronts.
        var cull = State("CullMode", "CCW") switch { "NONE" => CullMode.None, "CW" => CullMode.Front, _ => CullMode.Back };

        return new GpuPass
        {
            Name = pass.Pass.Name,
            Vs = new VertexShader(_device, pass.VertexShader),
            Ps = new PixelShader(_device, pass.PixelShader),
            InputLayout = new InputLayout(_device, pass.VertexShader, Layout),
            VsInfo = Reflect(pass.VertexShader),
            PsInfo = Reflect(pass.PixelShader),
            VsCb = ConstantBuffer(Reflect(pass.VertexShader).CbSize),
            PsCb = ConstantBuffer(Reflect(pass.PixelShader).CbSize),
            Blend = new BlendState(_device, blend),
            Depth = new DepthStencilState(_device, new DepthStencilStateDescription
            {
                IsDepthEnabled = depthTest, DepthWriteMask = depthWrite ? DepthWriteMask.All : DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual,
            }),
            Raster = Raster(cull, FillMode.Solid),
        };
    }

    private static BlendOption BlendFactor(string d3d9) => d3d9 switch
    {
        "ZERO" => BlendOption.Zero,
        "SRCCOLOR" => BlendOption.SourceColor,
        "INVSRCCOLOR" => BlendOption.InverseSourceColor,
        "SRCALPHA" => BlendOption.SourceAlpha,
        "INVSRCALPHA" => BlendOption.InverseSourceAlpha,
        "DESTALPHA" => BlendOption.DestinationAlpha,
        "INVDESTALPHA" => BlendOption.InverseDestinationAlpha,
        "DESTCOLOR" => BlendOption.DestinationColor,
        "INVDESTCOLOR" => BlendOption.InverseDestinationColor,
        _ => BlendOption.One,
    };

    private Buffer ConstantBuffer(int size) =>
        new(_device, Math.Max(16, (size + 15) / 16 * 16), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

    private static StageInfo Reflect(byte[] bytecode)
    {
        using var reflection = new ShaderReflection(bytecode);
        int size = 0;
        var variables = new List<(string, int, int, byte[]?)>();
        for (int c = 0; c < reflection.Description.ConstantBuffers; c++)
        {
            var cb = reflection.GetConstantBuffer(c);
            if (cb.Description.Name != "$Globals") continue;
            size = cb.Description.Size;
            for (int v = 0; v < cb.Description.VariableCount; v++)
            {
                var d = cb.GetVariable(v).Description;
                byte[]? def = null;
                if (d.DefaultValue != IntPtr.Zero)
                {
                    def = new byte[d.Size];
                    Marshal.Copy(d.DefaultValue, def, 0, d.Size);
                }
                variables.Add((d.Name, d.StartOffset, d.Size, def));
            }
        }
        var bindings = new List<(string, int, ShaderInputType, ShaderResourceViewDimension)>();
        for (int i = 0; i < reflection.Description.BoundResources; i++)
        {
            var b = reflection.GetResourceBindingDescription(i);
            if (b.Type is ShaderInputType.Texture or ShaderInputType.Sampler)
                bindings.Add((b.Name, b.BindPoint, b.Type, b.Dimension));
        }
        return new StageInfo { CbSize = size, Variables = variables, Bindings = bindings };
    }

    private GpuItem Upload(GameSceneItem item, IReadOnlyList<GpuPass> passes)
    {
        var mesh = item.Mesh;
        var vertices = new Vertex[Math.Max(1, mesh.Vertices.Count)];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = mesh.Vertices[i];
            vertices[i] = new Vertex
            {
                Position = v.Position, Normal = v.Normal, Tangent = v.Tangent, Binormal = Vector3.Cross(v.Normal, v.Tangent),
                Uv0 = v.Uv, Uv1 = v.Uv, Color = 0xFFFFFFFF,
            };
        }
        var groups = new List<(Buffer, int, int)>();
        foreach (var group in mesh.Triangles.GroupBy(t => t.Material).OrderBy(g => g.Key))
        {
            var indices = group.Where(t => t.A < mesh.Vertices.Count && t.B < mesh.Vertices.Count && t.C < mesh.Vertices.Count)
                .SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
            if (indices.Length == 0) continue;
            groups.Add((Buffer.Create(_device, BindFlags.IndexBuffer, indices), indices.Length, group.Key));
        }
        return new GpuItem
        {
            Item = item, Vertices = Buffer.Create(_device, BindFlags.VertexBuffer, vertices), Groups = groups, Passes = passes, Samplers = item.Effect.Samplers,
        };
    }

    private void BuildMarkers(MeshData mesh)
    {
        if (mesh.Points.Count == 0) return;
        float size = Math.Max(0.4f, mesh.BoundingRadius / 60f);
        foreach (var kind in mesh.Points.GroupBy(p => p.Name.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) ? 0
                     : p.Name.StartsWith("Exhaust", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
        {
            var builder = new HelixToolkit.Geometry.MeshBuilder(true, false);
            foreach (var point in kind)
            {
                builder.AddSphere(point.Position, size, 8, 8);
                if (point.Forward != Vector3.Zero) builder.AddArrow(point.Position, point.Position + point.Forward * size * 5, size * 0.35f, 2, 8);
            }
            var geometry = builder.ToMesh();
            var vertices = geometry.Positions!.Select((p, i) => new Vertex { Position = p, Normal = geometry.Normals![i], Color = 0xFFFFFFFF }).ToArray();
            int[] indices = [.. geometry.TriangleIndices!];
            var color = kind.Key switch { 0 => new Vector4(1f, 0.25f, 0.2f, 1f), 1 => new Vector4(0.3f, 0.6f, 1f, 1f), _ => new Vector4(1f, 0.85f, 0.2f, 1f) };
            _markers.Add((Buffer.Create(_device, BindFlags.VertexBuffer, vertices), Buffer.Create(_device, BindFlags.IndexBuffer, indices), indices.Length, color));
        }
    }

    // ---- Textures ----

    private ShaderResourceView Solid(byte r, byte g, byte b, byte a) => TextureView(new PixelImage(1, 1, [b, g, r, a]));

    private ShaderResourceView TextureView(PixelImage image)
    {
        if (_textures.TryGetValue(image, out var view)) return view;
        int mips = 1 + (int)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height)));
        var texture = new Texture2D(_device, new Texture2DDescription
        {
            Width = image.Width, Height = image.Height, MipLevels = mips, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
        });
        _context.UpdateSubresource(image.Bgra, texture, 0, image.Stride);
        view = new ShaderResourceView(_device, texture);
        _context.GenerateMips(view);
        texture.Dispose();
        return _textures[image] = view;
    }

    private ShaderResourceView CubeView(CubeMap cube)
    {
        if (_cubes.TryGetValue(cube, out var view)) return view;
        var texture = new Texture2D(_device, new Texture2DDescription
        {
            Width = cube.Size, Height = cube.Size, MipLevels = 1, ArraySize = 6, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            OptionFlags = ResourceOptionFlags.TextureCube,
        });
        for (int face = 0; face < 6; face++) _context.UpdateSubresource(cube.Faces[face].Bgra, texture, face, cube.Faces[face].Stride);
        view = new ShaderResourceView(_device, texture, new ShaderResourceViewDescription
        {
            Format = Format.B8G8R8A8_UNorm, Dimension = ShaderResourceViewDimension.TextureCube,
            TextureCube = new ShaderResourceViewDescription.TextureCubeResource { MipLevels = 1, MostDetailedMip = 0 },
        });
        texture.Dispose();
        return _cubes[cube] = view;
    }

    /// <summary>
    /// Smooth tiling fractal noise in every channel (each its own pattern), for effects that sample the engine's 3D noise
    /// texture (g_TextureNoise3D). Per-texel random values would show up as blocks.
    /// </summary>
    private ShaderResourceView Noise3D(int size)
    {
        var data = new byte[size * size * size * 4];
        var random = new Random(1);
        for (int channel = 0; channel < 4; channel++)
        {
            var field = new float[size * size * size];
            float amplitude = 0.5f, total = 0;
            for (int cells = 4; cells <= size / 2; cells *= 2, amplitude *= 0.5f)
            {
                var lattice = new float[cells * cells * cells];
                for (int i = 0; i < lattice.Length; i++) lattice[i] = (float)random.NextDouble();
                float L(int x, int y, int z) => lattice[((z % cells) * cells + (y % cells)) * cells + (x % cells)];
                static float Smooth(float t) => t * t * (3 - 2 * t);
                for (int z = 0; z < size; z++)
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float fx = (float)x * cells / size, fy = (float)y * cells / size, fz = (float)z * cells / size;
                    int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
                    float tx = Smooth(fx - x0), ty = Smooth(fy - y0), tz = Smooth(fz - z0);
                    float Lerp(float a, float b, float t) => a + (b - a) * t;
                    float v = Lerp(
                        Lerp(Lerp(L(x0, y0, z0), L(x0 + 1, y0, z0), tx), Lerp(L(x0, y0 + 1, z0), L(x0 + 1, y0 + 1, z0), tx), ty),
                        Lerp(Lerp(L(x0, y0, z0 + 1), L(x0 + 1, y0, z0 + 1), tx), Lerp(L(x0, y0 + 1, z0 + 1), L(x0 + 1, y0 + 1, z0 + 1), tx), ty), tz);
                    field[(z * size + y) * size + x] += v * amplitude;
                }
                total += amplitude;
            }
            for (int i = 0; i < field.Length; i++) data[i * 4 + channel] = (byte)Math.Clamp(field[i] / total * 255f, 0, 255);
        }
        using var texture = new Texture3D(_device, new Texture3DDescription
        {
            Width = size, Height = size, Depth = size, MipLevels = 1, Format = Format.B8G8R8A8_UNorm, Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _context.UpdateSubresource(data, texture, 0, size * 4, size * size * 4);
        return new ShaderResourceView(_device, texture);
    }

    private SamplerState Sampler(bool clamp, bool point)
    {
        if (_samplers.TryGetValue((clamp, point), out var sampler)) return sampler;
        var address = clamp ? TextureAddressMode.Clamp : TextureAddressMode.Wrap;
        return _samplers[(clamp, point)] = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = point ? Filter.MinMagMipPoint : Filter.Anisotropic, MaximumAnisotropy = 8,
            AddressU = address, AddressV = address, AddressW = address, MaximumLod = float.MaxValue, ComparisonFunction = Comparison.Never,
        });
    }

    /// <summary>What a shader texture variable reads, by the names the game's effects use.</summary>
    private ShaderResourceView TextureFor(string texture, ShaderResourceViewDimension dimension, GameMaterial? material,
        IReadOnlyDictionary<string, PixelImage>? extra)
    {
        if (dimension == ShaderResourceViewDimension.Texture3D) return _noise3D;
        if (extra != null && extra.TryGetValue(texture, out var supplied)) return TextureView(supplied);
        if (dimension == ShaderResourceViewDimension.TextureCube)
        {
            bool illumination = texture.Contains("Illumination", StringComparison.OrdinalIgnoreCase) || texture.Contains("Irradiance", StringComparison.OrdinalIgnoreCase);
            var map = illumination ? _scene?.Irradiance ?? _scene?.Environment : _scene?.Environment;
            return map is null ? _greyCube : CubeView(map);
        }
        return texture switch
        {
            "g_TextureDiffuse0" => material?.Diffuse is { } d ? TextureView(d) : _grey,
            "g_TextureNormal" => material?.Normal is { } n ? TextureView(n) : _flatNormal,
            "g_TextureSelfIllumination" => material?.Data is { } s ? TextureView(s) : _black,
            "g_TextureTeamColor" => material?.TeamColor is { } t ? TextureView(t) : _black,
            "g_TextureDisplacement" => material?.Displacement is { } h ? TextureView(h) : _black,
            "g_TextureShadowMap" => _shadowResource,
            _ when texture.Contains("Normal", StringComparison.OrdinalIgnoreCase) => _flatNormal,
            // Unknown textures (e.g. a planet without a cloud layer) add nothing rather than paint over the surface.
            _ => _black,
        };
    }

    // ---- Cel shading: the editor's own shaders (not the game's), for icon-like pictures ----

    private const string CelShaders = """
        cbuffer Cel : register(b0)
        {
            row_major float4x4 World;
            row_major float4x4 WorldViewProjection;
            row_major float4x4 WorldView;
            float4 FillColor;
            float4 Light;      // xyz: direction to the sun (world), w: bands
            float4 Params;     // x: shadow darkness, y: use texture, z: team colour, w: unused
            float4 Team;
        };
        Texture2D Diffuse : register(t0);
        SamplerState DiffuseSampler : register(s0);

        struct VI { float3 p : POSITION; float3 n : NORMAL; float3 t : TANGENT; float3 b : BINORMAL; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; float4 c : COLOR; };
        struct VO { float4 p : SV_Position; float3 n : NORMAL; float2 uv : TEXCOORD0; float3 vn : TEXCOORD1; float vz : TEXCOORD2; };

        VO CelVS(VI i)
        {
            VO o;
            o.p = mul(float4(i.p, 1), WorldViewProjection);
            o.n = mul(float4(i.n, 0), World).xyz;
            o.vn = mul(float4(i.n, 0), WorldView).xyz;
            o.vz = mul(float4(i.p, 1), WorldView).z;
            o.uv = i.uv;
            return o;
        }

        float4 CelPS(VO i) : SV_Target
        {
            float3 n = normalize(i.n);
            float lit = saturate(dot(n, normalize(Light.xyz)));
            float bands = max(1, Light.w);
            float shade = bands <= 1 ? 1 : min(floor(lit * bands), bands - 1) / (bands - 1);
            float brightness = lerp(1 - Params.x, 1, shade);
            float4 fill = FillColor;
            if (Params.y > 0.5)
            {
                float4 t = Diffuse.Sample(DiffuseSampler, i.uv);
                fill = float4(lerp(t.rgb, Team.rgb, t.a * Params.z), 1);
            }
            return float4(fill.rgb * brightness, 1);
        }

        // Normals (view space) and view depth for finding lines; w = 0 where there's no mesh.
        float4 GeometryPS(VO i) : SV_Target { return float4(normalize(i.vn), max(i.vz, 1e-3)); }

        cbuffer Edge : register(b0)
        {
            float4 LineColor;
            float4 LineParams;   // x: width (px; lines reach half of it to each side), y: cos of the crease angle, z: inner lines, w: relative depth jump
        };
        Texture2D<float4> Geometry : register(t0);

        struct EO { float4 p : SV_Position; float2 uv : TEXCOORD0; };
        EO EdgeVS(uint id : SV_VertexID)
        {
            EO o;
            float2 t = float2((id << 1) & 2, id & 2);
            o.p = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = t;
            return o;
        }

        float Differs(float4 c, float4 s)
        {
            bool covered = c.w > 0, other = s.w > 0;
            if (covered != other) return 1;
            if (!covered || LineParams.z < 0.5) return 0;
            if (abs(s.w - c.w) > LineParams.w * min(c.w, s.w)) return 1;
            return dot(c.xyz, s.xyz) < LineParams.y ? 1 : 0;
        }

        float4 EdgePS(EO i) : SV_Target
        {
            int2 size;
            Geometry.GetDimensions(size.x, size.y);
            int2 px = int2(i.p.xy);
            float4 c = Geometry.Load(int3(px, 0));
            float width = max(0.5, LineParams.x);
            float edge = 0;
            [unroll] for (int k = 0; k < 8; k++)
            {
                float a = k * 0.785398;
                float2 d = float2(cos(a), sin(a));
                [unroll] for (int r = 1; r <= 2; r++)
                {
                    int2 q = clamp(px + int2(round(d * width * r / 4)), int2(0, 0), size - 1);
                    edge = max(edge, Differs(c, Geometry.Load(int3(q, 0))));
                }
            }
            return float4(LineColor.rgb, LineColor.a * edge);
        }

        // The same lines as coverage for transparent pictures.
        float4 EdgeMaskPS(EO i) : SV_Target { float4 e = EdgePS(i); return float4(1, 1, 1, e.a); }
        """;

    private VertexShader? _celVs, _edgeVs;
    private PixelShader? _celPs, _geometryPs, _edgePs, _edgeMaskPs;
    private InputLayout? _celLayout;
    private Buffer? _celCb;
    private BlendState? _alphaBlend;

    private void EnsureCelShaders()
    {
        if (_celVs != null) return;
        byte[] Compile(string entry, string profile)
        {
            using var result = ShaderBytecode.Compile(CelShaders, entry, profile, ShaderFlags.OptimizationLevel3);
            return result.Bytecode.Data;
        }
        byte[] vs = Compile("CelVS", "vs_4_0");
        _celVs = new VertexShader(_device, vs);
        _celLayout = new InputLayout(_device, vs, Layout);
        _celPs = new PixelShader(_device, Compile("CelPS", "ps_4_0"));
        _geometryPs = new PixelShader(_device, Compile("GeometryPS", "ps_4_0"));
        _edgeVs = new VertexShader(_device, Compile("EdgeVS", "vs_4_0"));
        _edgePs = new PixelShader(_device, Compile("EdgePS", "ps_4_0"));
        _edgeMaskPs = new PixelShader(_device, Compile("EdgeMaskPS", "ps_4_0"));
        _celCb = new Buffer(_device, 256, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var blend = BlendStateDescription.Default();
        blend.RenderTarget[0].IsBlendEnabled = true;
        blend.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        blend.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        blend.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        blend.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        _alphaBlend = new BlendState(_device, blend);
    }

    private void DisposeCelShaders()
    {
        _celVs?.Dispose(); _edgeVs?.Dispose(); _celPs?.Dispose(); _geometryPs?.Dispose(); _edgePs?.Dispose(); _edgeMaskPs?.Dispose();
        _celLayout?.Dispose(); _celCb?.Dispose(); _alphaBlend?.Dispose();
    }

    /// <summary>
    /// The meshes in the cel style: their geometry (normals, depth) first into a separate target for the lines, then the
    /// fill in light bands into the colour target. Lines are drawn by <see cref="DrawCelLines"/> afterwards.
    /// </summary>
    private void DrawCel(CelStyle style, Matrix4x4 cameraOffset, Matrix4x4 view, Matrix4x4 projection, Vector3 lightDirection, Vector4 team, float time)
    {
        EnsureCelShaders();
        var fill = CelStyle.ParseColor(style.FillColor) ?? Vector4.One;

        void Draw(PixelShader ps, DepthStencilState depth)
        {
            foreach (var item in _items)
            {
                var world = item.Item.PlacementAtTime(time) * cameraOffset;
                var data = new float[64];
                M(world).CopyTo(data, 0);
                M(world * view * projection).CopyTo(data, 16);
                M(world * view).CopyTo(data, 32);
                V(fill).CopyTo(data, 48);
                V(new Vector4(lightDirection, Math.Clamp(style.Bands, 1, 8))).CopyTo(data, 52);
                V(new Vector4((float)Math.Clamp(style.Shadow, 0, 1), style.UseTexture ? 1 : 0, style.TeamColor ? 1 : 0, 0)).CopyTo(data, 56);
                V(team).CopyTo(data, 60);
                _context.UpdateSubresource(data, _celCb);
                _context.InputAssembler.InputLayout = _celLayout;
                _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(item.Vertices, Stride, 0));
                _context.VertexShader.Set(_celVs);
                _context.VertexShader.SetConstantBuffer(0, _celCb);
                _context.PixelShader.Set(ps);
                _context.PixelShader.SetConstantBuffer(0, _celCb);
                _context.PixelShader.SetSampler(0, Sampler(false, false));
                _context.OutputMerger.SetBlendState(_opaque);
                _context.OutputMerger.SetDepthStencilState(depth);
                _context.Rasterizer.State = _noCull;
                foreach (var (indices, count, materialIndex) in item.Groups)
                {
                    var material = materialIndex < item.Item.Materials.Count ? item.Item.Materials[materialIndex] : null;
                    _context.PixelShader.SetShaderResource(0, material?.Diffuse is { } diffuse ? TextureView(diffuse) : _white);
                    _context.InputAssembler.SetIndexBuffer(indices, Format.R32_UInt, 0);
                    _context.DrawIndexed(count, 0, 0);
                }
            }
        }

        // Geometry for the lines, without multisampling so every pixel has one exact normal and depth.
        _context.OutputMerger.SetTargets(_geometryDepthView, _geometryView);
        _context.ClearRenderTargetView(_geometryView, new RawColor4(0, 0, 0, 0));
        _context.ClearDepthStencilView(_geometryDepthView, DepthStencilClearFlags.Depth, 1f, 0);
        Draw(_geometryPs!, _depthOn);

        _context.OutputMerger.SetTargets(_depthView, _colorView);
        Draw(_celPs!, _depthOn);
        _context.PixelShader.SetShaderResource(0, null);
    }

    /// <summary>Lines where the geometry target changes (outline, creases, overlaps), blended over the colour (or mask) target.</summary>
    private void DrawCelLines(CelStyle style, RenderTargetView target, bool mask, float scale)
    {
        if (!style.Outline) return;
        var color = CelStyle.ParseColor(style.LineColor) ?? new Vector4(0, 0, 0, 1);
        var data = new float[64];
        V(color).CopyTo(data, 0);
        V(new Vector4((float)Math.Max(0.5, style.LineWidth * scale), MathF.Cos((float)Math.Clamp(style.CreaseAngle, 1, 179) * MathF.PI / 180),
            style.InnerLines ? 1 : 0, 0.04f)).CopyTo(data, 4);
        _context.UpdateSubresource(data, _celCb);
        _context.OutputMerger.SetTargets((DepthStencilView?)null, target);
        _context.InputAssembler.InputLayout = null;
        _context.VertexShader.Set(_edgeVs);
        _context.PixelShader.Set(mask ? _edgeMaskPs : _edgePs);
        _context.PixelShader.SetConstantBuffer(0, _celCb);
        _context.PixelShader.SetShaderResource(0, _geometryResource);
        _context.OutputMerger.SetBlendState(_alphaBlend);
        _context.OutputMerger.SetDepthStencilState(_noDepth);
        _context.Rasterizer.State = _noCull;
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResource(0, null);
        _context.OutputMerger.SetTargets(_depthView, _colorView);
    }
    // ---- Rendering ----

    private void EnsureTargets(int width, int height)
    {
        if (width == _width && height == _height && _color != null) return;
        DisposeTargets();
        _width = width;
        _height = height;
        _color = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(Msaa, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        _colorView = new RenderTargetView(_device, _color);
        _depthBuffer = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(Msaa, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
        });
        _depthView = new DepthStencilView(_device, _depthBuffer);
        _resolved = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
        });
        // Coverage of the meshes, for transparent pictures.
        _mask = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(Msaa, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        _maskView = new RenderTargetView(_device, _mask);
        _maskResolved = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
        });
        _maskStaging = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read,
        });
        _staging = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read,
        });
        _geometry = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _geometryView = new RenderTargetView(_device, _geometry);
        _geometryResource = new ShaderResourceView(_device, _geometry);
        _geometryDepth = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
        });
        _geometryDepthView = new DepthStencilView(_device, _geometryDepth);
    }

    private void DisposeTargets()
    {
        _geometryView?.Dispose(); _geometryResource?.Dispose(); _geometry?.Dispose(); _geometryDepthView?.Dispose(); _geometryDepth?.Dispose();
        _colorView?.Dispose(); _color?.Dispose(); _depthView?.Dispose(); _depthBuffer?.Dispose(); _resolved?.Dispose(); _staging?.Dispose();
        _maskView?.Dispose(); _mask?.Dispose(); _maskResolved?.Dispose(); _maskStaging?.Dispose();
        _colorView = null; _color = null;
    }

    private static Matrix4x4 LookAtLH(Vector3 eye, Vector3 target, Vector3 up)
    {
        var z = Vector3.Normalize(target - eye);
        var x = Vector3.Normalize(Vector3.Cross(up, z));
        var y = Vector3.Cross(z, x);
        return new Matrix4x4(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0, -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1);
    }

    private static Matrix4x4 PerspectiveLH(float fovY, float aspect, float near, float far)
    {
        float yScale = 1f / MathF.Tan(fovY / 2), xScale = yScale / aspect;
        return new Matrix4x4(xScale, 0, 0, 0, 0, yScale, 0, 0, 0, 0, far / (far - near), 1, 0, 0, -near * far / (far - near), 0);
    }

    private static Matrix4x4 OrthoLH(float width, float height, float near, float far) =>
        new(2 / width, 0, 0, 0, 0, 2 / height, 0, 0, 0, 0, 1 / (far - near), 0, 0, 0, near / (near - far), 1);

    /// <summary>Renders the scene and returns BGRA pixels, rows top to bottom (<paramref name="width"/> × 4 bytes each).</summary>
    public byte[] Render(int width, int height, OrbitCamera camera, GameRenderOptions options)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        EnsureTargets(width, height);
        var scene = _scene;
        var primary = scene?.Primary;
        float radius = primary is null ? 1 : Math.Max(1f, primary.BoundingRadius);
        var center = primary is null ? Vector3.Zero : (primary.MinExtents + primary.MaxExtents) / 2;

        // The light: above, in front and to the left of the ship, fixed in the world so turning the model shows its shading.
        var lightDirection = options.LightDirection is { } chosen && chosen.LengthSquared() > 1e-8f ? Vector3.Normalize(chosen) : GameRenderOptions.DefaultLightDirection;
        var lightPosition = center + lightDirection * Math.Max(radius * 50, scene?.StarDistance ?? 100_000f);
        var lightView = LookAtLH(center + lightDirection * radius * 3, center, Math.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
        var lightProjection = OrthoLH(radius * 2.2f, radius * 2.2f, 0, radius * 6);
        var shadowBias = new Matrix4x4(0.5f, 0, 0, 0, 0, -0.5f, 0, 0, 0, 0, 1, 0, 0.5f, 0.5f, 0, 1);
        if (options.Cel is null) RenderShadowMap(lightView * lightProjection, options.Time);

        // The game's shaders take world positions relative to the camera (the camera sits at the origin).
        var eye = camera.Eye;
        var view = LookAtLH(Vector3.Zero, camera.Target - eye, Vector3.UnitY);
        // Depth range around the model (with room for atmosphere shells and attached meshes): a near plane close to the
        // camera wastes the depth buffer's precision and makes a planet's surface and cloud shell fight.
        float distanceToCenter = Vector3.Distance(eye, center);
        float near = Math.Max(distanceToCenter * 0.002f, distanceToCenter - radius * 2f), far = distanceToCenter + radius * 2f;
        var projection = PerspectiveLH(camera.FieldOfViewRadians, (float)width / height, near, far);
        var cameraOffset = Matrix4x4.CreateTranslation(-eye);

        _context.OutputMerger.SetTargets(_depthView, _colorView);
        _context.Rasterizer.SetViewport(new RawViewportF { X = 0, Y = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 });
        // Space behind a skybox is black; without one, the editor's dark grey; transparent pictures start empty.
        _context.ClearRenderTargetView(_colorView, options.Transparent ? new RawColor4(0, 0, 0, 0)
            : _skybox is null && (options.Background ?? scene?.Background) is null ? new RawColor4(0.118f, 0.118f, 0.133f, 1f) : new RawColor4(0, 0, 0, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;

        if (!options.Transparent && (options.Background ?? scene?.Background) is { } background) DrawBackground(background, width, height);
        else if (!options.Transparent && options.Background is null && _skybox is { } sky)
        {
            // Around the camera, between the clipping planes; its passes draw without depth. The camera is inside it, looking
            // at its inner faces, so nothing is culled.
            float scale = (near + far) / 2 / Math.Max(1f, sky.Item.Mesh.BoundingRadius);
            DrawItem(sky, Matrix4x4.CreateScale(scale), view, projection, eye, Matrix4x4.Identity, scene!, lightPosition, radius, noCull: true);
        }

        if (options.Cel is { } cel && scene != null) DrawCel(cel, cameraOffset, view, projection, lightDirection, scene.TeamColor, options.Time);
        foreach (var item in _items)
        {
            var placement = item.Item.PlacementAtTime(options.Time);
            var world = placement * cameraOffset;
            if (options.Cel is null)
                DrawItem(item, world, view, projection, eye, placement * lightView * lightProjection * shadowBias, scene!, lightPosition, radius);
            if (options.Wireframe)
                DrawHelper(item.Vertices, item.Groups.Select(g => (g.Indices, g.Count)), world * view * projection, new Vector4(0.35f, 0.8f, 1f, 1f), _wirePs, _wireRaster, _depthRead);
        }

        if (options.Transparent) RenderMask(cameraOffset * view * projection, options.Time);
        if (options.Cel is { } style)
        {
            DrawCelLines(style, _colorView!, mask: false, options.LineScale);
            if (options.Transparent) DrawCelLines(style, _maskView!, mask: true, options.LineScale);
        }
        if (options.ShowParticles && scene?.Particles is { } particles)
        {
            _context.OutputMerger.SetTargets(_depthView, _colorView);
            DrawParticles(particles, view, projection, eye);
        }

        if (options.ShowPoints)
        {
            var wvp = cameraOffset * view * projection;
            foreach (var marker in _markers)
                DrawHelper(marker.Vertices, [(marker.Indices, marker.Count)], wvp, marker.Color, _flatPs, _noCull, _depthOn);
        }

        _context.ResolveSubresource(_color, 0, _resolved, 0, Format.B8G8R8A8_UNorm);
        _context.CopyResource(_resolved, _staging);
        var box = _context.MapSubresource(_staging, 0, MapMode.Read, MapFlags.None);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Marshal.Copy(box.DataPointer + y * box.RowPitch, pixels, y * width * 4, width * 4);
        _context.UnmapSubresource(_staging, 0);
        if (options.Transparent) ApplyMask(pixels, width, height);
        if (_device.DeviceRemovedReason.Failure)
            throw new SharpDX.SharpDXException(_device.DeviceRemovedReason, "The GPU stopped drawing (a shader took too long or failed).");
        return pixels;
    }

    /// <summary>A fixed 2D picture filling the view (cropped to keep its proportions), behind everything.</summary>
    private void DrawBackground(PixelImage image, int width, int height)
    {
        float imageAspect = (float)image.Width / image.Height, viewAspect = (float)width / height;
        var scale = imageAspect > viewAspect ? new Vector2(viewAspect / imageAspect, 1) : new Vector2(1, imageAspect / viewAspect);
        var offset = (Vector2.One - scale) / 2;
        var data = new float[20];
        data[16] = scale.X; data[17] = scale.Y; data[18] = offset.X; data[19] = offset.Y;
        _context.UpdateSubresource(data, _helperCb);
        _context.InputAssembler.InputLayout = null;
        _context.VertexShader.Set(_backgroundVs);
        _context.VertexShader.SetConstantBuffer(0, _helperCb);
        _context.PixelShader.Set(_backgroundPs);
        _context.PixelShader.SetShaderResource(0, TextureView(image));
        _context.PixelShader.SetSampler(0, Sampler(true, false));
        _context.OutputMerger.SetBlendState(_opaque);
        _context.OutputMerger.SetDepthStencilState(_noDepth);
        _context.Rasterizer.State = _noCull;
        _context.Draw(3, 0);
    }

    /// <summary>Mesh coverage as white on black, using the depth of the colour pass (only visible surfaces), for the alpha channel.</summary>
    private void RenderMask(Matrix4x4 cameraViewProjection, float time)
    {
        _context.OutputMerger.SetTargets(_depthView, _maskView);
        _context.ClearRenderTargetView(_maskView, new RawColor4(0, 0, 0, 0));
        foreach (var item in _items)
            DrawHelper(item.Vertices, item.Groups.Select(g => (g.Indices, g.Count)), item.Item.PlacementAtTime(time) * cameraViewProjection, Vector4.One, _wirePs, _noCull, _depthRead);
        _context.OutputMerger.SetTargets(_depthView, _colorView);
    }

    /// <summary>Alpha = mesh coverage, or the brightness of what particles added; colours are un-premultiplied.</summary>
    private void ApplyMask(byte[] pixels, int width, int height)
    {
        _context.ResolveSubresource(_mask, 0, _maskResolved, 0, Format.R8G8B8A8_UNorm);
        _context.CopyResource(_maskResolved, _maskStaging);
        var box = _context.MapSubresource(_maskStaging, 0, MapMode.Read, MapFlags.None);
        var mask = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Marshal.Copy(box.DataPointer + y * box.RowPitch, mask, y * width * 4, width * 4);
        _context.UnmapSubresource(_maskStaging, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = Math.Max(mask[i], Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])));
            if (alpha > 0 && alpha < 255)
            {
                for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / alpha);
            }
            pixels[i + 3] = (byte)alpha;
        }
    }

    /// <summary>
    /// Particle sprites: camera-facing quads in camera-relative world space (the particle effects' vertex shaders take
    /// world positions and g_ViewProjection), drawn with their effect's passes and blend states, sorted back to front.
    /// </summary>
    private void DrawParticles(IReadOnlyList<GameParticleEffect> effects, Matrix4x4 view, Matrix4x4 projection, Vector3 eye)
    {
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var forward = new Vector3(view.M13, view.M23, view.M33);
        foreach (var effect in effects)
        {
            for (int e = 0; e < effect.Emitters.Count; e++)
            {
                if (effect.Emitters[e] is not { } emitter) continue;
                IReadOnlyList<GpuPass> passes;
                try { passes = EffectPasses(emitter.Effect); }
                catch (Exception ex) when (ex is ShaderCompileException or SharpDX.SharpDXException) { Error ??= ex.Message; continue; }

                var particles = effect.System.Particles.Where(p => p.Emitter == e && p.Color.W > 0.001f && p.Width > 0)
                    .Select(p => (Particle: p, Center: Vector3.Transform(p.Position, effect.Placement) - eye))
                    .OrderByDescending(x => Vector3.Dot(x.Center, forward)).ToList();
                if (particles.Count == 0) continue;

                var vertices = new Vertex[particles.Count * 4];
                var indices = new int[particles.Count * 6];
                for (int i = 0; i < particles.Count; i++)
                {
                    var (p, center) = particles[i];
                    float cos = MathF.Cos(p.Rotation), sin = MathF.Sin(p.Rotation);
                    var axisX = (right * cos + up * sin) * p.Width / 2;
                    var axisY = (up * cos - right * sin) * p.Height / 2;
                    var c = p.Color;
                    uint color = (uint)(Math.Clamp(c.X, 0, 1) * 255) | (uint)(Math.Clamp(c.Y, 0, 1) * 255) << 8
                               | (uint)(Math.Clamp(c.Z, 0, 1) * 255) << 16 | (uint)(Math.Clamp(c.W, 0, 1) * 255) << 24;
                    vertices[i * 4] = new Vertex { Position = center - axisX + axisY, Uv0 = new(0, 0), Uv1 = new(0, 0), Color = color, Normal = -forward };
                    vertices[i * 4 + 1] = new Vertex { Position = center + axisX + axisY, Uv0 = new(1, 0), Uv1 = new(1, 0), Color = color, Normal = -forward };
                    vertices[i * 4 + 2] = new Vertex { Position = center + axisX - axisY, Uv0 = new(1, 1), Uv1 = new(1, 1), Color = color, Normal = -forward };
                    vertices[i * 4 + 3] = new Vertex { Position = center - axisX - axisY, Uv0 = new(0, 1), Uv1 = new(0, 1), Color = color, Normal = -forward };
                    int[] quad = [0, 1, 2, 0, 2, 3];
                    for (int k = 0; k < 6; k++) indices[i * 6 + k] = i * 4 + quad[k];
                }
                using var vertexBuffer = Buffer.Create(_device, BindFlags.VertexBuffer, vertices);
                using var indexBuffer = Buffer.Create(_device, BindFlags.IndexBuffer, indices);
                var textures = new Dictionary<string, PixelImage>();
                if (emitter.Texture0 is { } t0) { textures["g_TextureDiffuse0"] = t0; textures["g_TextureDiffuse1"] = emitter.Texture1 ?? t0; }
                var values = new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["g_ViewProjection"] = M(view * projection),
                    ["g_World"] = M(Matrix4x4.Identity),
                    ["g_WorldViewProjection"] = M(view * projection),
                    ["g_View"] = M(view),
                    ["g_Projection"] = M(projection),
                    ["colorMultiplier"] = [1f],
                };
                _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, Stride, 0));
                _context.InputAssembler.SetIndexBuffer(indexBuffer, Format.R32_UInt, 0);
                foreach (var pass in passes)
                {
                    _context.InputAssembler.InputLayout = pass.InputLayout;
                    _context.VertexShader.Set(pass.Vs);
                    _context.PixelShader.Set(pass.Ps);
                    FillConstants(pass.VsInfo, pass.VsCb, values);
                    FillConstants(pass.PsInfo, pass.PsCb, values);
                    _context.VertexShader.SetConstantBuffer(0, pass.VsCb);
                    _context.PixelShader.SetConstantBuffer(0, pass.PsCb);
                    BindResources(pass.VsInfo, emitter.Effect.Samplers, null, textures, _context.VertexShader);
                    BindResources(pass.PsInfo, emitter.Effect.Samplers, null, textures, _context.PixelShader);
                    _context.OutputMerger.SetBlendState(pass.Blend);
                    _context.OutputMerger.SetDepthStencilState(_depthRead);
                    _context.Rasterizer.State = _noCull;
                    _context.DrawIndexed(indices.Length, 0, 0);
                }
            }
        }
    }

    /// <summary>Draws a mesh with every pass of its effect, filling the engine's constants by name.</summary>
    private void DrawItem(GpuItem item, Matrix4x4 world, Matrix4x4 view, Matrix4x4 projection, Vector3 eye, Matrix4x4 shadowUv, GameScene scene,
        Vector3 lightPosition, float radius, bool noCull = false)
    {
        var values = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["g_World"] = M(world),
            ["g_WorldViewProjection"] = M(world * view * projection),
            ["g_View"] = M(view),
            ["g_ViewProjection"] = M(view * projection),
            ["g_Projection"] = M(projection),
            ["g_WorldView"] = M(world * view),
            ["g_ShadowUVTransforms"] = M(shadowUv),
            ["g_Light0_Position"] = V(lightPosition - eye),
            ["g_Light0_DiffuseLite"] = [1f, 1f, 1f, 1f],
            ["g_Light0_DiffuseDark"] = [0.1f, 0.1f, 0.12f, 1f],
            ["g_Light0_Specular"] = [1f, 1f, 1f, 1f],
            ["g_Light_AmbientLite"] = [0.35f, 0.35f, 0.38f, 1f],
            ["g_Light_AmbientDark"] = [0.12f, 0.12f, 0.14f, 1f],
            ["g_TeamColor"] = V(scene.TeamColor),
            ["g_ShadowTextureSize"] = [ShadowSize],
            ["g_ShadowNormalBias"] = [radius * 0.002f],
            ["g_ShadowDepthBias"] = [4f],
            ["g_Time"] = [0f],
            ["g_Radius"] = [item.Item.Mesh.BoundingRadius],
            ["colorMultiplier"] = [1f],
        };
        foreach (var (name, value) in item.Item.Constants ?? new Dictionary<string, Vector4>()) values[name] = V(value);

        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(item.Vertices, Stride, 0));
        foreach (var (indices, count, materialIndex) in item.Groups)
        {
            var material = materialIndex < item.Item.Materials.Count ? item.Item.Materials[materialIndex] : null;
            values["g_MaterialGlossiness"] = [material?.Glossiness ?? 50f];
            values["g_MaterialDiffuse"] = V(material?.DiffuseColor ?? Vector4.One);
            values["g_MaterialAmbient"] = V(material?.AmbientColor ?? Vector4.One);
            values["g_MaterialSpecular"] = V(material?.SpecularColor ?? Vector4.One);
            values["g_MaterialEmissive"] = V(material?.EmissiveColor ?? Vector4.One);
            _context.InputAssembler.SetIndexBuffer(indices, Format.R32_UInt, 0);
            foreach (var pass in item.Passes)
            {
                if (item.Item.Passes is { } chosen && !chosen.Contains(pass.Name)) continue;
                _context.InputAssembler.InputLayout = pass.InputLayout;
                _context.VertexShader.Set(pass.Vs);
                _context.PixelShader.Set(pass.Ps);
                FillConstants(pass.VsInfo, pass.VsCb, values);
                FillConstants(pass.PsInfo, pass.PsCb, values);
                _context.VertexShader.SetConstantBuffer(0, pass.VsCb);
                _context.PixelShader.SetConstantBuffer(0, pass.PsCb);
                BindResources(pass.VsInfo, item.Samplers, material, item.Item.Textures, _context.VertexShader);
                BindResources(pass.PsInfo, item.Samplers, material, item.Item.Textures, _context.PixelShader);
                _context.OutputMerger.SetBlendState(pass.Blend);
                _context.OutputMerger.SetDepthStencilState(pass.Depth);
                _context.Rasterizer.State = noCull ? _noCull : pass.Raster;
                _context.DrawIndexed(count, 0, 0);
            }
        }
        // The shadow map is both a render target and an input; unbind it before the next shadow pass.
        _context.PixelShader.SetShaderResources(0, 16, new ShaderResourceView[16]);
    }

    private void RenderShadowMap(Matrix4x4 lightViewProjection, float time)
    {
        _context.OutputMerger.SetTargets(_shadowDepthView, _shadowView);
        _context.Rasterizer.SetViewport(new RawViewportF { X = 0, Y = 0, Width = ShadowSize, Height = ShadowSize, MinDepth = 0, MaxDepth = 1 });
        _context.ClearRenderTargetView(_shadowView, new RawColor4(1, 1, 1, 1));
        _context.ClearDepthStencilView(_shadowDepthView, DepthStencilClearFlags.Depth, 1f, 0);
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        foreach (var item in _items)
            DrawHelper(item.Vertices, item.Groups.Select(g => (g.Indices, g.Count)), item.Item.PlacementAtTime(time) * lightViewProjection, Vector4.One, _depthPs, _noCull, _depthOn);
    }

    private void DrawHelper(Buffer vertices, IEnumerable<(Buffer Indices, int Count)> groups, Matrix4x4 wvp, Vector4 color, PixelShader ps,
        RasterizerState raster, DepthStencilState depth)
    {
        var data = new float[20];
        M(wvp).CopyTo(data, 0);
        V(color).CopyTo(data, 16);
        _context.UpdateSubresource(data, _helperCb);
        _context.InputAssembler.InputLayout = _helperLayout;
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertices, Stride, 0));
        _context.VertexShader.Set(_helperVs);
        _context.VertexShader.SetConstantBuffer(0, _helperCb);
        _context.PixelShader.Set(ps);
        _context.PixelShader.SetConstantBuffer(0, _helperCb);
        _context.OutputMerger.SetBlendState(_opaque);
        _context.OutputMerger.SetDepthStencilState(depth);
        _context.Rasterizer.State = raster;
        foreach (var (indices, count) in groups)
        {
            _context.InputAssembler.SetIndexBuffer(indices, Format.R32_UInt, 0);
            _context.DrawIndexed(count, 0, 0);
        }
    }

    private void FillConstants(StageInfo info, Buffer buffer, Dictionary<string, float[]> values)
    {
        if (info.CbSize == 0) return;
        var data = new byte[(info.CbSize + 15) / 16 * 16];
        foreach (var (name, offset, size, def) in info.Variables)
        {
            if (values.TryGetValue(name, out var value))
                System.Buffer.BlockCopy(value, 0, data, offset, Math.Min(size, value.Length * 4));
            else if (def != null)
                System.Buffer.BlockCopy(def, 0, data, offset, Math.Min(size, def.Length));
        }
        _context.UpdateSubresource(data, buffer);
    }

    private void BindResources(StageInfo info, IReadOnlyDictionary<string, EffectSampler> samplers, GameMaterial? material,
        IReadOnlyDictionary<string, PixelImage>? extra, CommonShaderStage stage)
    {
        foreach (var (name, slot, type, dimension) in info.Bindings)
        {
            // Bindings carry the sampler's name; the effect's sampler_state says which texture it reads.
            var sampler = samplers.GetValueOrDefault(name);
            if (type == ShaderInputType.Sampler) stage.SetSampler(slot, Sampler(sampler?.Clamp ?? false, sampler?.Point ?? false));
            else stage.SetShaderResource(slot, TextureFor(sampler?.Texture is { Length: > 0 } t ? t : name,
                sampler?.IsCube == true ? ShaderResourceViewDimension.TextureCube : dimension, material, extra));
        }
    }

    private static float[] M(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
    private static float[] V(Vector4 v) => [v.X, v.Y, v.Z, v.W];

    public void Dispose()
    {
        _skybox?.Dispose();
        foreach (var item in _items) item.Dispose();
        foreach (var m in _markers) { m.Vertices.Dispose(); m.Indices.Dispose(); }
        foreach (var passes in _effects.Values) foreach (var p in passes) p.Dispose();
        foreach (var v in _textures.Values) v.Dispose();
        foreach (var v in _cubes.Values) v.Dispose();
        foreach (var s in _samplers.Values) s.Dispose();
        DisposeTargets();
        _shadowView.Dispose(); _shadowResource.Dispose(); _shadow.Dispose(); _shadowDepthView.Dispose(); _shadowDepth.Dispose();
        _helperVs.Dispose(); _flatPs.Dispose(); _wirePs.Dispose(); _depthPs.Dispose(); _helperLayout.Dispose(); _helperCb.Dispose();
        _solid.Dispose(); _noCull.Dispose(); _wireRaster.Dispose(); _depthOn.Dispose(); _depthRead.Dispose(); _opaque.Dispose();
        _noise3D.Dispose();
        DisposeCelShaders();
        _noDepth.Dispose(); _backgroundVs?.Dispose(); _backgroundPs?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
