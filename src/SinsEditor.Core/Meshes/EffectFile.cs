using System.Text.RegularExpressions;

namespace SinsEditor.Core.Meshes;

/// <summary>How a sampler of an effect reads its texture.</summary>
public sealed record EffectSampler(string Name, string Texture, bool IsCube, bool Clamp, bool Point);

/// <summary>A pass of an effect technique: the shader functions it compiles and its render states.</summary>
public sealed record EffectPass(string Name, string? VertexFunction, string? VertexProfile, string? PixelFunction, string? PixelProfile,
    IReadOnlyDictionary<string, string> States)
{
    public bool HasShaders => VertexFunction != null && PixelFunction != null;
}

public sealed record EffectTechnique(string Name, IReadOnlyList<EffectPass> Passes);

/// <summary>
/// A game shader (<c>PipelineEffect/*.fx</c>, DirectX 9 effect syntax) taken apart so a renderer can compile its functions:
/// the source without technique blocks, the techniques with their passes, and which texture each sampler reads.
/// </summary>
public sealed partial class EffectFile
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    /// <summary>The source with the technique blocks removed, ready to compile single functions.</summary>
    public required string ShaderSource { get; init; }
    public required IReadOnlyList<EffectTechnique> Techniques { get; init; }
    public required IReadOnlyDictionary<string, EffectSampler> Samplers { get; init; }

    /// <summary>The technique a renderer with pixel shaders uses: one whose first pass compiles shaders, preferably RenderWithPixelShader.</summary>
    public EffectTechnique? ShaderTechnique =>
        Techniques.FirstOrDefault(t => t.Name == "RenderWithPixelShader" && t.Passes.Count > 0 && t.Passes[0].HasShaders)
        ?? Techniques.FirstOrDefault(t => t.Passes.Count > 0 && t.Passes[0].HasShaders);

    [GeneratedRegex(@"\btechnique\s+(\w+)\s*\{")]
    private static partial Regex TechniqueStart();

    [GeneratedRegex(@"\bpass\s+(\w+)\s*\{")]
    private static partial Regex PassStart();

    [GeneratedRegex(@"(VertexShader|PixelShader)\s*=\s*compile\s+(\w+)\s+(\w+)\s*\(")]
    private static partial Regex CompileStatement();

    [GeneratedRegex(@"(\w+)\s*=\s*([^;]+);")]
    private static partial Regex State();

    [GeneratedRegex(@"\b(sampler(?:CUBE|2D|3D)?)\s+(\w+)\s*=\s*sampler_state\s*\{(.*?)\}\s*;", RegexOptions.Singleline)]
    private static partial Regex SamplerBlock();

    // HLSL effect keywords are case-insensitive: some shaders write "texture = <…>".
    [GeneratedRegex(@"\bTexture\s*=\s*<\s*(\w+)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex SamplerTexture();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    public static EffectFile Parse(string name, string source)
    {
        var techniques = new List<EffectTechnique>();
        var withoutTechniques = new System.Text.StringBuilder();
        int last = 0;
        foreach (Match m in TechniqueStart().Matches(source))
        {
            if (m.Index < last) continue;
            int end = MatchingBrace(source, m.Index + m.Length - 1);
            string body = source[(m.Index + m.Length)..Math.Max(m.Index + m.Length, end)];
            techniques.Add(new EffectTechnique(m.Groups[1].Value, ParsePasses(body)));
            withoutTechniques.Append(source, last, m.Index - last);
            last = Math.Min(source.Length, end + 1);
        }
        withoutTechniques.Append(source, last, source.Length - last);

        var samplers = new Dictionary<string, EffectSampler>(StringComparer.Ordinal);
        foreach (Match m in SamplerBlock().Matches(Comments().Replace(source, "")))
        {
            string body = m.Groups[3].Value;
            string texture = SamplerTexture().Match(body) is { Success: true } t ? t.Groups[1].Value : "";
            bool clamp = body.Contains("CLAMP", StringComparison.OrdinalIgnoreCase);
            bool point = Regex.IsMatch(body, @"(Min|Mag)?Filter\s*=\s*POINT", RegexOptions.IgnoreCase);
            samplers[m.Groups[2].Value] = new EffectSampler(m.Groups[2].Value, texture, m.Groups[1].Value == "samplerCUBE", clamp, point);
        }
        return new EffectFile { Name = name, Source = source, ShaderSource = withoutTechniques.ToString(), Techniques = techniques, Samplers = samplers };
    }

    private static List<EffectPass> ParsePasses(string technique)
    {
        var passes = new List<EffectPass>();
        foreach (Match p in PassStart().Matches(technique))
        {
            int end = MatchingBrace(technique, p.Index + p.Length - 1);
            string body = Comments().Replace(technique[(p.Index + p.Length)..Math.Max(p.Index + p.Length, end)], "");
            string? vs = null, vsProfile = null, ps = null, psProfile = null;
            foreach (Match c in CompileStatement().Matches(body))
            {
                if (c.Groups[1].Value == "VertexShader") (vsProfile, vs) = (c.Groups[2].Value, c.Groups[3].Value);
                else (psProfile, ps) = (c.Groups[2].Value, c.Groups[3].Value);
            }
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match s in State().Matches(CompileStatement().Replace(body, "")))
                states[s.Groups[1].Value] = s.Groups[2].Value.Trim();
            passes.Add(new EffectPass(p.Groups[1].Value, vs, vsProfile, ps, psProfile, states));
        }
        return passes;
    }

    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return text.Length;
    }
}

/// <summary>Which game shader draws a mesh, and where shader files come from.</summary>
public static class GameShaders
{
    public const string Ship = "GS_ShadowedShip.fx";

    /// <summary>Every shader of the effective mod (PipelineEffect folder of all layers).</summary>
    public static IReadOnlyList<string> All(ModWorkspace workspace) =>
        workspace.Stack.EnumerateFolder("PipelineEffect", ".fx").Select(f => Path.GetFileName(f.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The shader the game draws a mesh with, from the entities that use it: planets GS_Planet, stars GS_Star, asteroids
    /// GS_ShadowedAsteroid, skyboxes GS_Skybox; ships and everything else GS_ShadowedShip.
    /// </summary>
    public static string ForMesh(ModWorkspace workspace, string meshName)
    {
        string name = Path.GetFileNameWithoutExtension(meshName);
        var types = workspace.Index.IncomingReferences(Index.ReferenceKind.Mesh, name)
            .Concat(workspace.Index.IncomingReferences(Index.ReferenceKind.Mesh, name + ".mesh"))
            .Select(r => r.Source.EntityType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Asteroid belts are Planet entities too, but their meshes are drawn as asteroids.
        if (name.Contains("Asteroid", StringComparison.OrdinalIgnoreCase)) return "GS_ShadowedAsteroid.fx";
        if (types.Contains("Planet") || name.StartsWith("Planet_", StringComparison.OrdinalIgnoreCase)) return "GS_Planet.fx";
        if (types.Contains("Star")) return "GS_Star.fx";
        if (name.StartsWith("Skybox", StringComparison.OrdinalIgnoreCase)) return "GS_Skybox.fx";
        return Ship;
    }

    /// <summary>Loads a shader through the layers, so a mod's own version wins. Null if no layer has it.</summary>
    public static EffectFile? Load(ModWorkspace workspace, string fxName) =>
        workspace.Stack.Resolve("PipelineEffect/" + fxName) is { } file ? EffectFile.Parse(fxName, File.ReadAllText(file.AbsolutePath)) : null;

    /// <summary>
    /// Stands in for effects that only have a fixed-function technique (the older particle mesh effects draw the diffuse
    /// texture unlit): the same, as shaders.
    /// </summary>
    public static EffectFile Unlit(string name) => EffectFile.Parse(name, """
        float4x4 g_WorldViewProjection;
        texture g_TextureDiffuse0;
        sampler TextureColorSampler = sampler_state { Texture = <g_TextureDiffuse0>; };
        struct VsOutput { float4 Position : POSITION; float2 TexCoord : TEXCOORD0; };
        VsOutput RenderSceneVS(float3 position : POSITION, float2 texCoord : TEXCOORD0)
        {
            VsOutput o;
            o.Position = mul(float4(position, 1.f), g_WorldViewProjection);
            o.TexCoord = texCoord;
            return o;
        }
        float4 RenderScenePS(VsOutput i) : COLOR0 { return tex2D(TextureColorSampler, i.TexCoord); }
        technique RenderWithPixelShader
        {
            pass Pass0
            {
                VertexShader = compile vs_2_0 RenderSceneVS();
                PixelShader = compile ps_2_0 RenderScenePS();
            }
        }
        """);

    /// <summary>The effect to draw with: the shader file through the layers, or <see cref="Unlit"/> if it has no shader technique.</summary>
    public static EffectFile ForDrawing(ModWorkspace workspace, string fxName)
    {
        var effect = Load(workspace, fxName);
        return effect?.ShaderTechnique != null ? effect : Unlit(effect is null ? $"{fxName} (missing, drawn unlit)" : $"{fxName} (fixed function, drawn unlit)");
    }

    /// <summary>The skyboxes whose environment cube maps exist (skyboxNNenvironmentcube + skyboxNNirradiance).</summary>
    public static IReadOnlyList<string> Skyboxes(ModWorkspace workspace) =>
        workspace.Index.Textures.Keys
            .Where(k => k.EndsWith("environmentcube", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[..^"environmentcube".Length])
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
}
