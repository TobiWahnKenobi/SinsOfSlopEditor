using SinsEditor.Core.Meshes;

namespace SinsEditor.Tests.Meshes;

public sealed class ParticleSystemTests
{
    /// <summary>The emitter of the game's Flair_Blink.particle: one short flash every 1/0.3 s.</summary>
    private static ParticleEffectDefinition Blink(float startTime) => ParticleEffectDefinition.Parse(
    [
        "TXT2", "ParticleSimulation", "\tHasInfiniteLifeTime TRUE", "\tTotalLifeTime 10.000000", "\tNumEmitters 1",
        "\tEmitterType \"Point\"", "\tEmitterContents", "\t\tName \"Light\"", "\t\tEnabled TRUE", "\t\tEmitRate 0.300000",
        "\t\tHasInfiniteEmitCount TRUE", "\t\tMaxEmitCount 0", "\t\thasEmitIntervals FALSE", "\t\tParticleLifeTime 0.300000",
        "\t\tPosition [ 0.000000 0.000000 0.000000 ]", "\t\tOrientation", "\t\t\t [ 1.000000 0.000000 0.000000 ]",
        "\t\t\t [ 0.000000 1.000000 0.000000 ]", "\t\t\t [ 0.000000 0.000000 1.000000 ]",
        "\t\tStartTime " + startTime.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), "\t\tHasInfiniteLifeTime TRUE", "\tNumAffectors 0",
    ]);

    private static bool LitAt(ParticleSystem system, float t)
    {
        system.RunTo(t);
        return system.Particles.Count > 0;
    }

    [Fact]
    public void A_blink_light_flashes_at_its_start_time_and_then_every_few_seconds()
    {
        var system = new ParticleSystem(Blink(0));
        Assert.True(LitAt(system, 0.1f));
        Assert.False(LitAt(system, 1f));
        Assert.False(LitAt(system, 3f));
        Assert.True(LitAt(system, 3.5f));
        Assert.False(LitAt(system, 4.5f));
        Assert.True(LitAt(system, 6.8f));
        // Going back replays the same.
        Assert.True(LitAt(system, 0.1f));
    }

    [Fact]
    public void Emitters_wait_for_their_start_time()
    {
        var system = new ParticleSystem(Blink(2));
        Assert.False(LitAt(system, 1.9f));
        Assert.True(LitAt(system, 2.1f));
        Assert.False(LitAt(system, 3f));
    }
}
