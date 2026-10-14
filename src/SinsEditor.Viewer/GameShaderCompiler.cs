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
        var technique = effect.ShaderTechnique ?? throw new ShaderCompileException($"{effect.Name} has no technique with shaders.");
        var passes = new List<CompiledPass>();
        foreach (var pass in technique.Passes.Where(p => p.HasShaders))
            passes.Add(new CompiledPass(CompileFunction(effect, pass.VertexFunction!, "vs_4_0"), CompileFunction(effect, pass.PixelFunction!, "ps_4_0"), pass));
        return passes;
    }

    public static byte[] CompileFunction(EffectFile effect, string function, string profile)
    {
        string source = effect.ShaderSource;
        if (profile.StartsWith("ps", StringComparison.Ordinal) && VposWrapper(source, function) is { } wrapper)
            (source, function) = (source + wrapper.Code, wrapper.Entry);
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
