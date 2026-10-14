using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using SinsEditor.Core.Meshes;
using SinsEditor.Core.Textures;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace SinsEditor.Viewer;

/// <summary>Display options of the game-shader view.</summary>
public sealed record GameRenderOptions(bool ShowPoints, bool Wireframe);

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
    private readonly DepthStencilState _depthOn, _depthRead;
    private readonly BlendState _opaque;
    private GameScene? _scene;

    // Targets, recreated on resize.
    private int _width, _height;
    private Texture2D? _color, _resolved, _staging, _depthBuffer;
    private RenderTargetView? _colorView;
    private DepthStencilView? _depthView;

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
        _staging = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read,
        });
    }

    private void DisposeTargets()
    {
        _colorView?.Dispose(); _color?.Dispose(); _depthView?.Dispose(); _depthBuffer?.Dispose(); _resolved?.Dispose(); _staging?.Dispose();
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
        var lightDirection = Vector3.Normalize(new Vector3(-0.6f, 0.9f, 0.8f));
        var lightPosition = center + lightDirection * Math.Max(radius * 50, scene?.StarDistance ?? 100_000f);
        var lightView = LookAtLH(center + lightDirection * radius * 3, center, Math.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
        var lightProjection = OrthoLH(radius * 2.2f, radius * 2.2f, 0, radius * 6);
        var shadowBias = new Matrix4x4(0.5f, 0, 0, 0, 0, -0.5f, 0, 0, 0, 0, 1, 0, 0.5f, 0.5f, 0, 1);
        RenderShadowMap(lightView * lightProjection);

        // The game's shaders take world positions relative to the camera (the camera sits at the origin).
        var eye = camera.Eye;
        var view = LookAtLH(Vector3.Zero, camera.Target - eye, Vector3.UnitY);
        // Depth range around the model (with room for atmosphere shells and attached meshes): a near plane close to the
        // camera wastes the depth buffer's precision and makes a planet's surface and cloud shell fight.
        float distanceToCenter = Vector3.Distance(eye, center);
        float near = Math.Max(distanceToCenter * 0.002f, distanceToCenter - radius * 2f), far = distanceToCenter + radius * 2f;
        var projection = PerspectiveLH(45f * MathF.PI / 180, (float)width / height, near, far);
        var cameraOffset = Matrix4x4.CreateTranslation(-eye);

        _context.OutputMerger.SetTargets(_depthView, _colorView);
        _context.Rasterizer.SetViewport(new RawViewportF { X = 0, Y = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 });
        // Space behind a skybox is black; without one, the editor's dark grey.
        _context.ClearRenderTargetView(_colorView, _skybox is null ? new RawColor4(0.118f, 0.118f, 0.133f, 1f) : new RawColor4(0, 0, 0, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;

        if (_skybox is { } sky)
        {
            // Around the camera, between the clipping planes; its passes draw without depth. The camera is inside it, looking
            // at its inner faces, so nothing is culled.
            float scale = (near + far) / 2 / Math.Max(1f, sky.Item.Mesh.BoundingRadius);
            DrawItem(sky, Matrix4x4.CreateScale(scale), view, projection, eye, Matrix4x4.Identity, scene!, lightPosition, radius, noCull: true);
        }

        foreach (var item in _items)
        {
            var world = item.Item.Placement * cameraOffset;
            DrawItem(item, world, view, projection, eye, item.Item.Placement * lightView * lightProjection * shadowBias, scene!, lightPosition, radius);
            if (options.Wireframe)
                DrawHelper(item.Vertices, item.Groups.Select(g => (g.Indices, g.Count)), world * view * projection, new Vector4(0.35f, 0.8f, 1f, 1f), _wirePs, _wireRaster, _depthRead);
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
        if (_device.DeviceRemovedReason.Failure)
            throw new SharpDX.SharpDXException(_device.DeviceRemovedReason, "The GPU stopped drawing (a shader took too long or failed).");
        return pixels;
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

    private void RenderShadowMap(Matrix4x4 lightViewProjection)
    {
        _context.OutputMerger.SetTargets(_shadowDepthView, _shadowView);
        _context.Rasterizer.SetViewport(new RawViewportF { X = 0, Y = 0, Width = ShadowSize, Height = ShadowSize, MinDepth = 0, MaxDepth = 1 });
        _context.ClearRenderTargetView(_shadowView, new RawColor4(1, 1, 1, 1));
        _context.ClearDepthStencilView(_shadowDepthView, DepthStencilClearFlags.Depth, 1f, 0);
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        foreach (var item in _items)
            DrawHelper(item.Vertices, item.Groups.Select(g => (g.Indices, g.Count)), item.Item.Placement * lightViewProjection, Vector4.One, _depthPs, _noCull, _depthOn);
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
        _context.Dispose();
        _device.Dispose();
    }
}
