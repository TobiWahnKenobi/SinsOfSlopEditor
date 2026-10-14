using System.Globalization;
using System.Numerics;

namespace SinsEditor.Core.Meshes;

/// <summary>
/// One emitter or affector block of a <c>.particle</c> file: its type and fields. Fields are kept by key (first value),
/// repeated keys like <c>textureName</c> or <c>attachedEmitterName</c> as lists.
/// </summary>
public sealed class ParticleBlock(string type)
{
    public string Type { get; } = type;
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Matrix4x4 Orientation { get; set; } = Matrix4x4.Identity;

    public string Text(string key, string fallback = "") => Fields.TryGetValue(key, out var v) ? Unquote(v) : fallback;
    public float Float(string key, float fallback = 0) =>
        Fields.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
    public bool Bool(string key, bool fallback = false) => Fields.TryGetValue(key, out var v) ? v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) : fallback;
    public Vector3 Vector(string key) => Fields.TryGetValue(key, out var v) ? MeshData.Vector(v) : default;
    public Vector4 Color(string key, Vector4 fallback) =>
        Fields.TryGetValue(key, out var v) && uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb) ? MeshMaterial.ToVector(argb) : fallback;
    public IReadOnlyList<string> List(string key) => Lists.TryGetValue(key, out var l) ? l : [];

    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;
}

/// <summary>A particle effect as written: its emitters and affectors.</summary>
public sealed class ParticleEffectDefinition
{
    public List<ParticleBlock> Emitters { get; } = [];
    public List<ParticleBlock> Affectors { get; } = [];
    public bool HasInfiniteLifeTime { get; private set; } = true;
    public float TotalLifeTime { get; private set; }

    public static ParticleEffectDefinition Load(string textPath) => Parse(File.ReadLines(textPath));

    public static ParticleEffectDefinition Parse(IEnumerable<string> lines)
    {
        var effect = new ParticleEffectDefinition();
        ParticleBlock? block = null;
        string? pendingType = null;
        int orientationRow = -1;
        var orientation = Matrix4x4.Identity;
        bool inEffectHeader = true;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                if (orientationRow >= 0 && block != null)
                {
                    orientation = MeshMath.WithRow(orientation, orientationRow, MeshData.Vector(line));
                    if (++orientationRow == 3) { block.Orientation = orientation; orientationRow = -1; }
                }
                continue;
            }
            int space = line.IndexOf(' ');
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (key)
            {
                case "EmitterType" or "AffectorType":
                    pendingType = MeshData.Unquote(value);
                    inEffectHeader = false;
                    continue;
                case "EmitterContents":
                    block = new ParticleBlock(pendingType ?? "Point");
                    effect.Emitters.Add(block);
                    continue;
                case "AffectorContents":
                    block = new ParticleBlock(pendingType ?? "");
                    effect.Affectors.Add(block);
                    continue;
                case "Orientation" when block != null && !block.Fields.ContainsKey("Orientation"):
                    block.Fields["Orientation"] = "";
                    orientationRow = 0;
                    orientation = Matrix4x4.Identity;
                    continue;
                case "HasInfiniteLifeTime" when inEffectHeader:
                    effect.HasInfiniteLifeTime = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                    continue;
                case "TotalLifeTime" when inEffectHeader:
                    effect.TotalLifeTime = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ? t : 0;
                    continue;
            }
            if (block is null) continue;
            if (key is "textureName" or "attachedEmitterName")
            {
                if (!block.Lists.TryGetValue(key, out var list)) block.Lists[key] = list = [];
                list.Add(MeshData.Unquote(value));
            }
            else block.Fields.TryAdd(key, value);
        }
        return effect;
    }
}

/// <summary>A live particle in effect space.</summary>
public struct Particle
{
    public Vector3 Position, Velocity;
    public float Age, LifeTime, Width, Height, Rotation, AngularSpeed, Mass;
    public Vector4 Color;
    public Vector4 BaseColor;
    public float BaseWidth, BaseHeight;
    public int Emitter;
    public bool Alive;
}

/// <summary>
/// Simulates a particle effect the way the editor shows it: emitters spawn particles (point emitters in a cone around their
/// forward axis, ring and sphere emitters on their shapes), affectors change them over their life. Deterministic for a seed,
/// so a still picture is reproducible.
/// </summary>
public sealed class ParticleSystem
{
    private const int MaxParticles = 4000;
    private readonly ParticleEffectDefinition _definition;
    private readonly int _seed;
    private Random _random;
    private readonly float[] _emitAccumulator;
    private readonly int[] _emitted;
    private readonly HashSet<string>[] _affectorTargets;

    public ParticleSystem(ParticleEffectDefinition definition, int seed = 1)
    {
        _definition = definition;
        _seed = seed;
        _random = new Random(seed);
        _emitAccumulator = new float[definition.Emitters.Count];
        _emitted = new int[definition.Emitters.Count];
        _affectorTargets = definition.Affectors
            .Select(a => a.List("attachedEmitterName").ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
        StartPhases();
    }

    /// <summary>
    /// In game every copy of an effect starts when its ship appears, so copies on different hardpoints emit out of step;
    /// a random emission phase per seed gives the same.
    /// </summary>
    private void StartPhases()
    {
        for (int e = 0; e < _emitAccumulator.Length; e++) _emitAccumulator[e] = (float)_random.NextDouble();
    }

    public ParticleEffectDefinition Definition => _definition;
    public List<Particle> Particles { get; } = [];
    public float Time { get; private set; }

    /// <summary>
    /// Runs the simulation to <paramref name="time"/> seconds in fixed steps. Going back restarts it and runs forward again:
    /// with the same seed the result is the same as if it had been run there directly.
    /// </summary>
    public void RunTo(float time, float step = 1f / 30)
    {
        if (time < Time - step / 2) Reset();
        while (Time + step <= time + step / 2) Step(step);
    }

    public void Reset()
    {
        _random = new Random(_seed);
        StartPhases();
        Array.Clear(_emitted);
        Particles.Clear();
        Time = 0;
    }

    public void Step(float dt)
    {
        if (!_definition.HasInfiniteLifeTime && _definition.TotalLifeTime > 0 && Time > _definition.TotalLifeTime) { Time += dt; return; }
        for (int e = 0; e < _definition.Emitters.Count; e++) Emit(e, dt);

        for (int i = 0; i < Particles.Count; i++)
        {
            var p = Particles[i];
            p.Age += dt;
            if (p.Age >= p.LifeTime) { p.Alive = false; Particles[i] = p; continue; }
            p.Color = p.BaseColor;
            p.Width = p.BaseWidth;
            p.Height = p.BaseHeight;
            for (int a = 0; a < _definition.Affectors.Count; a++)
            {
                var affector = _definition.Affectors[a];
                if (!Applies(a, affector, p)) continue;
                Affect(affector, ref p, dt);
            }
            p.BaseWidth = p.Width;
            p.BaseHeight = p.Height;
            p.Position += p.Velocity * dt;
            p.Rotation += p.AngularSpeed * dt;
            Particles[i] = p;
        }
        Particles.RemoveAll(p => !p.Alive);
        Time += dt;
    }

    private bool Applies(int index, ParticleBlock affector, in Particle p)
    {
        if (!affector.Bool("Enabled", true)) return false;
        if (Time < affector.Float("StartTime")) return false;
        if (!affector.Bool("HasInfiniteLifeTime", true) && Time > affector.Float("StartTime") + affector.Float("TotalLifeTime")) return false;
        if (affector.Bool("UseYoungParticleAffectThreshold") && p.Age < affector.Float("YoungParticleAffectThreshold")) return false;
        if (affector.Bool("UseOldParticleAffectThreshold") && p.Age > affector.Float("OldParticleAffectThreshold")) return false;
        var targets = _affectorTargets[index];
        return targets.Count == 0 || targets.Contains(_definition.Emitters[p.Emitter].Text("Name"));
    }

    private void Emit(int e, float dt)
    {
        var emitter = _definition.Emitters[e];
        if (!emitter.Bool("Enabled", true)) return;
        float start = emitter.Float("StartTime");
        if (Time < start) return;
        if (!emitter.Bool("HasInfiniteLifeTime", true) && emitter.Float("TotalLifeTime") > 0 && Time > start + emitter.Float("TotalLifeTime")) return;
        if (emitter.Bool("hasEmitIntervals"))
        {
            float run = emitter.Float("emitIntervalRunDuration"), wait = emitter.Float("emitIntervalWaitDuration");
            if (run + wait > 0 && (Time - start) % (run + wait) > run) return;
        }
        bool infinite = emitter.Bool("HasInfiniteEmitCount", true);
        int max = (int)emitter.Float("MaxEmitCount");
        // Effects that emit a fixed number (e.g. one rotating mesh section) emit it at once.
        if (!infinite && _emitted[e] >= max) return;
        float rate = emitter.Float("EmitRate");
        _emitAccumulator[e] += rate * dt;
        if (!infinite && rate <= 0) _emitAccumulator[e] = max;
        while (_emitAccumulator[e] >= 1 && Particles.Count < MaxParticles && (infinite || _emitted[e] < max))
        {
            _emitAccumulator[e] -= 1;
            _emitted[e]++;
            Particles.Add(Spawn(e, emitter));
        }
    }

    private float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    private Vector3 RandomUnit()
    {
        float z = Range(-1, 1), a = Range(0, MathF.Tau), r = MathF.Sqrt(1 - z * z);
        return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
    }

    private Particle Spawn(int index, ParticleBlock emitter)
    {
        var orientation = emitter.Orientation;
        var placement = MeshMath.Placement(orientation, emitter.Vector("Position"));
        var right = new Vector3(orientation.M11, orientation.M12, orientation.M13);
        var up = new Vector3(orientation.M21, orientation.M22, orientation.M23);
        var forward = new Vector3(orientation.M31, orientation.M32, orientation.M33);
        float speed = Range(emitter.Float("ParticleMinStartLinearSpeed"), emitter.Float("ParticleMaxStartLinearSpeed"));
        Vector3 local = Vector3.Zero, velocity;
        switch (emitter.Type)
        {
            case "Ring":
            {
                float angle = emitter.Bool("isSpawnAngleRandom", true)
                    ? Range(emitter.Float("SpawnAngleStart"), emitter.Float("SpawnAngleStop", MathF.Tau))
                    : emitter.Float("SpawnAngleStart") + (emitter.Float("SpawnAngleStop", MathF.Tau) - emitter.Float("SpawnAngleStart"))
                      * (_emitted[index] % Math.Max(1, (int)emitter.Float("nonRandomSpawnLoopEmittedParticleCount", 1))) / Math.Max(1, emitter.Float("nonRandomSpawnLoopEmittedParticleCount", 1));
                float rx = Range(emitter.Float("RingRadiusXMin"), emitter.Float("RingRadiusXMax"));
                float ry = Range(emitter.Float("RingRadiusYMin"), emitter.Float("RingRadiusYMax"));
                var radial = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                local = new Vector3(radial.X * rx, Range(emitter.Float("minSpawnHeight"), emitter.Float("maxSpawnHeight")), radial.Z * ry);
                var tangent = new Vector3(-radial.Z, 0, radial.X);
                float scale = emitter.Bool("ScaleStartSpeedsByRadius") ? MathF.Max(rx, ry) : 1;
                velocity = (radial * speed + tangent * Range(0, emitter.Float("ParticleMaxStartSpeedTangential"))
                            + Vector3.UnitY * Range(0, emitter.Float("ParticleMaxStartSpeedRingNormal"))) * scale;
                break;
            }
            case "Sphere":
            {
                var direction = RandomUnit();
                local = new Vector3(direction.X * Range(emitter.Float("SphereRadiusXMin"), emitter.Float("SphereRadiusXMax")),
                    direction.Y * Range(emitter.Float("SphereRadiusYMin"), emitter.Float("SphereRadiusYMax")),
                    direction.Z * Range(emitter.Float("SphereRadiusZMin"), emitter.Float("SphereRadiusZMax")));
                velocity = direction * speed;
                break;
            }
            default:
            {
                // A cone of AngleVariance (radians) around the emitter's forward axis.
                float variance = emitter.Float("AngleVariance");
                float cosMax = MathF.Cos(Math.Clamp(variance, 0, MathF.PI));
                float z = Range(cosMax, 1), a = Range(0, MathF.Tau), r = MathF.Sqrt(MathF.Max(0, 1 - z * z));
                velocity = new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z) * speed;
                break;
            }
        }
        // Emitter space → effect space: local axes are the orientation rows.
        var position = Vector3.Transform(local, placement);
        velocity = velocity.X * right + velocity.Y * up + velocity.Z * forward;
        float width = emitter.Float("ParticleWidth", 1), height = emitter.Float("ParticleHeight", 1);
        var color = emitter.Color("ParticleStartColor", Vector4.One);
        return new Particle
        {
            Position = position, Velocity = velocity, LifeTime = MathF.Max(0.01f, emitter.Float("ParticleLifeTime", 1)),
            Width = width, Height = height, BaseWidth = width, BaseHeight = height,
            Rotation = Range(emitter.Float("ParticleMinStartRotation"), emitter.Float("ParticleMaxStartRotation")),
            AngularSpeed = Range(emitter.Float("ParticleMinStartAngularSpeed"), emitter.Float("ParticleMaxStartAngularSpeed")),
            Mass = MathF.Max(0.0001f, emitter.Float("ParticleStartMass", 1)),
            Color = color, BaseColor = color, Emitter = index, Alive = true,
        };
    }

    private void Affect(ParticleBlock affector, ref Particle p, float dt)
    {
        switch (affector.Type)
        {
            case "Fade":
            {
                float alpha = 1;
                if (affector.Bool("DoFadeIn") && affector.Float("FadeInTime") > 0) alpha = MathF.Min(alpha, p.Age / affector.Float("FadeInTime"));
                if (affector.Bool("DoFadeOut") && affector.Float("FadeOutTime") > 0) alpha = MathF.Min(alpha, (p.LifeTime - p.Age) / affector.Float("FadeOutTime"));
                p.Color.W *= Math.Clamp(alpha, 0, 1);
                break;
            }
            case "LinearInflate":
                p.Width = MathF.Max(0, p.Width + affector.Float("WidthInflateRate") * dt);
                p.Height = MathF.Max(0, p.Height + affector.Float("HeightInflateRate") * dt);
                break;
            case "LinearForceToPoint":
            {
                var toPoint = affector.Vector("Point") - p.Position;
                if (toPoint.LengthSquared() > 1e-6f)
                    p.Velocity += Vector3.Normalize(toPoint) * Range(affector.Float("MinForce"), affector.Float("MaxForce")) / p.Mass * dt;
                break;
            }
            case "LinearForceInDirection":
            {
                var direction = affector.Vector("Direction");
                if (direction.LengthSquared() > 1e-6f)
                    p.Velocity += Vector3.Normalize(direction) * Range(affector.Float("MinForce"), affector.Float("MaxForce")) / p.Mass * dt;
                break;
            }
            case "Drag":
                p.Velocity *= MathF.Max(0, 1 - affector.Float("DragCoefficient") * dt);
                break;
            case "Jitter":
                p.Velocity += RandomUnit() * affector.Float("JitterForce") / p.Mass * dt;
                break;
            case "KillParticlesNearPoint":
                if (Vector3.Distance(p.Position, affector.Vector("Point")) < affector.Float("Distance")) p.Alive = false;
                break;
            case "RotateAboutAxis":
            {
                var axis = affector.Vector("AxisOfRotation");
                if (axis.LengthSquared() < 1e-6f) break;
                var origin = affector.Vector("AxisOrigin");
                var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), affector.Float("AngularVelocity") * dt);
                p.Position = Vector3.Transform(p.Position - origin, rotation) + origin;
                break;
            }
            case "SizeOscillator":
            {
                float t = Oscillate(p.Age, affector.Float("TransitionPeriod"));
                p.Width = Lerp(affector.Float("BeginSizeX", p.Width), affector.Float("EndSizeX", p.Width), t);
                p.Height = Lerp(affector.Float("BeginSizeY", p.Height), affector.Float("EndSizeY", p.Height), t);
                break;
            }
            case "ColorOscillator":
            {
                float t = Oscillate(p.Age, affector.Float("TransitionPeriod"));
                var start = affector.Color("StartColor", p.Color);
                var end = affector.Color("EndColor", p.Color);
                p.Color = new Vector4(Lerp(start.X, end.X, t), Lerp(start.Y, end.Y, t), Lerp(start.Z, end.Z, t),
                    p.Color.W * Lerp(affector.Float("StartAlpha", 1), affector.Float("EndAlpha", 1), t));
                break;
            }
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>0 → 1 → 0 over twice the transition period.</summary>
    private static float Oscillate(float age, float period)
    {
        if (period <= 0) return 0;
        float phase = age / period % 2;
        return phase <= 1 ? phase : 2 - phase;
    }
}
