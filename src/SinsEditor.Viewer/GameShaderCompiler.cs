using SharpDX.D3DCompiler;
using ShaderMacro = SharpDX.Direct3D.ShaderMacro;
using SinsEditor.Core.Meshes;

namespace SinsEditor.Viewer;

/// <summary>A compiled pass: vertex and pixel shader bytecode of a game effect, plus the pass's render states.</summary>
public sealed class CompiledPass(byte[] vertexShader, byte[] pixelShader, EffectPass pass)
{
    public byte[] VertexShader { get; } = vertexShader;
    public byte[] PixelShader { get; } = pixelShader;
    public EffectPass Pass { get; } = pass;
}

public sealed class ShaderCompileException(string message) : Exception(message);

/// <summary>
/// Compiles the functions of a game effect (DirectX 9 HLSL, shader model 2/3) for Direct3D 11 in the compiler's
/// backwards-compatibility mode, which accepts tex2D, sampler_state and POSITION/COLOR semantics. The engine's
/// compile-time defines are given: one shadow cascade.
/// </summary>
public static class GameShaderCompiler
{
    public static readonly ShaderMacro[] Defines =
    [
        new("ShadowMapCount", "1"),
        new("ShadowMapRowCount", "1"),
        new("ShadowMapColumnCount", "1"),
    ];

    private const ShaderFlags Flags = ShaderFlags.EnableBackwardsCompatibility | ShaderFlags.PackMatrixRowMajor | ShaderFlags.OptimizationLevel3;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, string Source), Lazy<IReadOnlyList<CompiledPass>>> Cache = new();

    /// <summary>
    /// Compiles every pass of the effect's shader technique, once per effect source (big effects take seconds; compile them
    /// ahead off the UI thread). Throws <see cref="ShaderCompileException"/> with the compiler's message.
    /// </summary>
    public static IReadOnlyList<CompiledPass> Compile(EffectFile effect) =>
        Cache.GetOrAdd((effect.Name, effect.Source), _ => new Lazy<IReadOnlyList<CompiledPass>>(() => CompileUncached(effect))).Value;

    private static IReadOnlyList<CompiledPass> CompileUncached(EffectFile effect)
    {
        var technique = effect.ShaderTechnique ?? effect.FixedFunctionTechnique
            ?? throw new ShaderCompileException($"{effect.Name} has no technique with shaders.");
        var passes = new List<CompiledPass>();
        foreach (var pass in technique.Passes.Where(p => p.VertexFunction != null))
        {
            byte[] vs = CompileFunction(effect, pass.VertexFunction!, "vs_4_0");
            byte[] ps = pass.PixelFunction is { } function
                ? CompileFunction(effect, function, "ps_4_0", VertexOutputs(vs))
                : FixedFunctionPixelShader(effect, pass, VertexOutputs(vs));
            passes.Add(new CompiledPass(vs, ps, pass));
        }
        return passes;
    }

    /// <summary>
    /// The pixel stage of a fixed-function pass (particle effects): the texture of <c>Texture[0]</c> times the vertex colour,
    /// as Direct3D 9's default texture stage does. Inputs are taken in the vertex shader's output order. The texture keeps its
    /// effect name (e.g. g_TextureDiffuse0), so it's bound like any other.
    /// </summary>
    private static byte[] FixedFunctionPixelShader(EffectFile effect, EffectPass pass,
        IReadOnlyList<(string Semantic, int Index, int Components, bool Position)> outputs)
    {
        string texture = pass.States.TryGetValue("Texture0", out var t) ? t : "g_TextureDiffuse0";
        var parameters = outputs.Select((o, i) => o.Position ? $"float4 v{i} : SV_Position"
            : $"float{(o.Components > 1 ? o.Components.ToString() : "")} v{i} : {o.Semantic}{o.Index}").ToList();
        int color = outputs.ToList().FindIndex(o => o.Semantic.Equals("COLOR", StringComparison.OrdinalIgnoreCase) && o.Index == 0);
        int uv = outputs.ToList().FindIndex(o => o.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && o.Index == 0 && o.Components >= 2);
        string colorExpr = color >= 0 ? (outputs[color].Components == 4 ? $"v{color}" : $"float4(v{color}.rgb, 1)") : "float4(1, 1, 1, 1)";
        string textureExpr = uv >= 0 ? $"{texture}.Sample({texture}Sampler, v{uv}.xy)" : "float4(1, 1, 1, 1)";
        string source = $$"""
            Texture2D {{texture}};
            SamplerState {{texture}}Sampler;
            float4 FixedFunctionPS({{string.Join(", ", parameters)}}) : SV_Target
            {
                return {{textureExpr}} * {{colorExpr}};
            }
            """;
        try
        {
            using var result = ShaderBytecode.Compile(source, "FixedFunctionPS", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None, null, null, effect.Name);
            if (result.Bytecode is null || result.HasErrors) throw new ShaderCompileException($"{effect.Name} fixed function: {result.Message}");
            return result.Bytecode.Data;
        }
        catch (SharpDX.CompilationException e)
        {
            throw new ShaderCompileException($"{effect.Name} fixed function: {e.Message}");
        }
    }

    /// <summary>
    /// Shader model 2/3 hardware stops a loop after 255 iterations; Direct3D 11 doesn't, so a loop whose bound turns out NaN
    /// (e.g. a mod's parallax skybox) runs until Windows resets the GPU. Loops with an int counter get the same cap.
    /// </summary>
    private static string CapLoops(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"\bfor\s*\(\s*int\s+(\w+)\s*=([^;]*);([^;]+);",
            m => $"for(int {m.Groups[1].Value} ={m.Groups[2].Value}; ({m.Groups[3].Value}) && ({m.Groups[1].Value} < 255);");

    /// <summary>What a vertex shader outputs, in register order: semantic (with index) and component count.</summary>
    private static List<(string Semantic, int Index, int Components, bool Position)> VertexOutputs(byte[] vertexShader)
    {
        using var reflection = new ShaderReflection(vertexShader);
        var outputs = new List<(string, int, int, bool)>();
        for (int i = 0; i < reflection.Description.OutputParameters; i++)
        {
            var o = reflection.GetOutputParameterDescription(i);
            int components = System.Numerics.BitOperations.PopCount((uint)o.UsageMask) is > 0 and var n ? n : 4;
            outputs.Add((o.SemanticName, o.SemanticIndex, Math.Max(components, BitWidth((uint)o.UsageMask)), o.SystemValueType == SystemValueType.Position));
        }
        return outputs;
    }

    private static int BitWidth(uint mask) => 32 - System.Numerics.BitOperations.LeadingZeroCount(mask);

    public static byte[] CompileFunction(EffectFile effect, string function, string profile,
        IReadOnlyList<(string Semantic, int Index, int Components, bool Position)>? vertexOutputs = null)
    {
        string source = CapLoops(effect.ShaderSource);
        if (profile.StartsWith("ps", StringComparison.Ordinal) && VposWrapper(source, function) is { } wrapper)
            (source, function) = (source + wrapper.Code, wrapper.Entry);
        else if (profile.StartsWith("ps", StringComparison.Ordinal) && vertexOutputs != null && LinkageWrapper(source, function, vertexOutputs) is { } linkage)
            (source, function) = (source + linkage.Code, linkage.Entry);
        // For looking at what is compiled (wrappers included): SINSEDITOR_SHADER_DUMP=<folder>.
        if (Environment.GetEnvironmentVariable("SINSEDITOR_SHADER_DUMP") is { Length: > 0 } dump)
            System.IO.File.WriteAllText(System.IO.Path.Combine(dump, $"{effect.Name}.{function}.{profile}.hlsl"), source);
        try
        {
            using var result = ShaderBytecode.Compile(source, function, profile, Flags, EffectFlags.None, Defines, null, effect.Name);
            if (result.Bytecode is null || result.HasErrors) throw new ShaderCompileException($"{effect.Name} {function}: {result.Message}");
            return result.Bytecode.Data;
        }
        catch (SharpDX.CompilationException e)
        {
            throw new ShaderCompileException($"{effect.Name} {function}: {e.Message}");
        }
    }

    /// <summary>
    /// Direct3D 9 links vertex outputs to pixel inputs by semantic; Direct3D 11 by register, in order. A pixel shader with
    /// separate parameters that leaves out the position (e.g. <c>RenderScenePS(float4 iColor : COLOR, float2 uv : TEXCOORD0, out
    /// float4 o : COLOR0)</c>) would read everything shifted. This makes an entry point that takes exactly what the vertex shader
    /// outputs, in its order, and passes each value on by semantic.
    /// </summary>
    private static (string Code, string Entry)? LinkageWrapper(string source, string function,
        IReadOnlyList<(string Semantic, int Index, int Components, bool Position)> outputs)
    {
        var signature = System.Text.RegularExpressions.Regex.Match(source,
            $@"\b(void|float4|half4)\s+{function}\s*\(([^)]*)\)\s*(?::\s*(\w+))?\s*\{{");
        if (!signature.Success) return null;
        var parameters = new List<(string Direction, string Type, string Name, string Semantic)>();
        // Parameters may be commented out inside the list.
        string list = System.Text.RegularExpressions.Regex.Replace(signature.Groups[2].Value, @"//[^\n]*|/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (string raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(?:(in|out|inout)\s+)?(\w+)\s+(\w+)\s*:\s*(\w+)$");
            if (!m.Success) return null;
            string type = m.Groups[2].Value;
            // Structs are linked as a whole already; only plain parameters are rewired.
            if (!System.Text.RegularExpressions.Regex.IsMatch(type, @"^(float|half|int|uint|bool)[1-4]?$")) return null;
            parameters.Add((m.Groups[1].Value, type, m.Groups[3].Value, m.Groups[4].Value.ToUpperInvariant()));
        }
        var inputs = parameters.Where(p => p.Direction != "out").ToList();
        if (inputs.Any(p => p.Semantic is "POSITION" or "POSITION0" or "VPOS" or "SV_POSITION")) return null;

        static (string Name, int Index) Split(string semantic)
        {
            int digits = semantic.Length;
            while (digits > 0 && char.IsDigit(semantic[digits - 1])) digits--;
            return (semantic[..digits], digits < semantic.Length ? int.Parse(semantic[digits..]) : 0);
        }

        var code = new System.Text.StringBuilder();
        string entry = "SinsEditor_Linked_" + function;
        var wrapperParams = outputs.Select((o, i) => o.Position
            ? $"float4 v{i} : SV_Position"
            : $"float{(o.Components > 1 ? o.Components.ToString() : "")} v{i} : {o.Semantic}{o.Index}");
        code.Append($"\nfloat4 {entry}({string.Join(", ", wrapperParams)}) : SV_Target\n{{\n");
        var args = new List<string>();
        string? outName = null;
        foreach (var p in parameters)
        {
            if (p.Direction == "out")
            {
                code.Append($"    {p.Type} {p.Name};\n");
                outName ??= p.Name;
                args.Add(p.Name);
                continue;
            }
            var (name, index) = Split(p.Semantic);
            int match = outputs.ToList().FindIndex(o => !o.Position && string.Equals(o.Semantic, name, StringComparison.OrdinalIgnoreCase) && o.Index == index);
            args.Add(match >= 0 ? $"({p.Type})v{match}" : $"({p.Type})0");
        }
        string call = $"{function}({string.Join(", ", args)})";
        if (signature.Groups[1].Value == "void")
        {
            if (outName is null) return null;
            code.Append($"    {call};\n    return (float4){outName};\n}}\n");
        }
        else code.Append($"    return {call};\n}}\n");
        return (code.ToString(), entry);
    }

    /// <summary>
    /// Direct3D 9 pixel shaders may take the pixel position as <c>VPOS</c> next to the vertex output's <c>POSITION</c>;
    /// Direct3D 11 has one SV_Position for both and rejects the pair. For <c>float4 F(Struct input, float2 p : VPOS)</c> this
    /// makes an entry point that takes only the struct and passes its POSITION member (pixel centres, so minus 0.5, as VPOS).
    /// </summary>
    private static (string Code, string Entry)? VposWrapper(string source, string function)
    {
        var signature = System.Text.RegularExpressions.Regex.Match(source,
            $@"\b(float4|half4)\s+{function}\s*\(\s*(\w+)\s+(\w+)\s*,\s*(float2|float4|half2|half4)\s+(\w+)\s*:\s*VPOS\s*\)\s*:\s*(\w+)");
        if (!signature.Success) return null;
        string type = signature.Groups[2].Value, vposType = signature.Groups[4].Value, outSemantic = signature.Groups[6].Value;
        var structBody = System.Text.RegularExpressions.Regex.Match(source, $@"\bstruct\s+{type}\s*\{{(.*?)\}}", System.Text.RegularExpressions.RegexOptions.Singleline);
        var member = structBody.Success
            ? System.Text.RegularExpressions.Regex.Match(structBody.Groups[1].Value, @"\b\w+\s+(\w+)\s*:\s*POSITION\d*\s*;")
            : System.Text.RegularExpressions.Match.Empty;
        if (!member.Success) return null;
        string entry = "SinsEditor_" + function;
        string position = vposType.EndsWith('4') ? $"float4(input.{member.Groups[1].Value}.xy - 0.5, 0, 0)" : $"input.{member.Groups[1].Value}.xy - 0.5";
        return ($"\nfloat4 {entry}({type} input) : {outSemantic} {{ return {function}(input, {position}); }}\n", entry);
    }
}
