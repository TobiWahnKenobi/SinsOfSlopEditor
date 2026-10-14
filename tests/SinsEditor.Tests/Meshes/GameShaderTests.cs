using SharpDX.D3DCompiler;
using SinsEditor.Core.Meshes;
using SinsEditor.Viewer;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Meshes;

public class GameShaderTests(ITestOutputHelper output)
{
    private static string? PipelineEffect => TestPaths.GameDir is { } g ? Path.Combine(g, "PipelineEffect") : null;

    [SkippableTheory]
    [InlineData("GS_ShadowedShip.fx")]
    [InlineData("GS_Ship.fx")]
    [InlineData("GS_ShadowedAsteroid.fx")]
    [InlineData("GS_Planet.fx")]
    public void Game_shaders_compile_for_direct3d11(string name)
    {
        Skip.If(PipelineEffect is null, "Game directory not configured.");
        var effect = EffectFile.Parse(name, File.ReadAllText(Path.Combine(PipelineEffect!, name)));
        var passes = GameShaderCompiler.Compile(effect);
        Assert.NotEmpty(passes);
        foreach (var pass in passes)
        {
            using var ps = new ShaderReflection(pass.PixelShader);
            for (int i = 0; i < ps.Description.BoundResources; i++)
            {
                var b = ps.GetResourceBindingDescription(i);
                output.WriteLine($"{name} PS {b.Type} {b.Name} slot {b.BindPoint} {b.Dimension}");
            }
            using var vs = new ShaderReflection(pass.VertexShader);
            for (int i = 0; i < vs.Description.InputParameters; i++)
            {
                var p = vs.GetInputParameterDescription(i);
                output.WriteLine($"{name} VS in {p.SemanticName}{p.SemanticIndex}");
            }
            foreach (var reflection in new[] { vs, ps })
            {
                for (int c = 0; c < reflection.Description.ConstantBuffers; c++)
                {
                    var cb = reflection.GetConstantBuffer(c);
                    for (int v = 0; v < cb.Description.VariableCount; v++)
                    {
                        var d = cb.GetVariable(v).Description;
                        output.WriteLine($"{name} cb {cb.Description.Name} {d.Name} @{d.StartOffset} {d.Size}");
                    }
                }
            }
        }
    }
}
