using SinsEditor.Core.Index;

namespace SinsEditor.Core.Validation;

/// <summary>
/// Checks beyond what indexing finds: entities against the learned <see cref="EntitySchema"/>, the mod's manifests,
/// unused entities, and what the AI needs to play each race (<see cref="AiReadinessCheck"/>). Entity results are kept per entity so a save only re-checks what changed.
/// </summary>
public sealed class Checks
{
    private readonly EntitySchema _schema;
    private readonly Dictionary<string, List<Problem>> _byEntity = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Problem> _manifests;
    private List<Problem> _unused;
    private List<Problem> _ai;

    private Checks(EntitySchema schema, List<Problem> manifests, List<Problem> unused, List<Problem> ai)
    {
        _schema = schema;
        _manifests = manifests;
        _unused = unused;
        _ai = ai;
    }

    public EntitySchema Schema => _schema;

    public IEnumerable<Problem> Problems => _byEntity.Values.SelectMany(p => p).Concat(_manifests).Concat(_unused).Concat(_ai);

    public static Checks Run(ModIndex index)
    {
        var checks = new Checks(EntitySchema.Learn(index), ManifestCheck.Check(index.Stack).ToList(), ManifestCheck.UnusedEntities(index).ToList(),
            AiReadinessCheck.Check(index).ToList());
        foreach (var entity in index.ActiveEntities.Where(e => e.File.Layer.IsEditable)) checks.CheckEntity(entity);
        return checks;
    }

    /// <summary>Re-checks an entity after it was saved.</summary>
    public void Update(ModIndex index, EntityRecord entity)
    {
        CheckEntity(entity);
        _unused = ManifestCheck.UnusedEntities(index).ToList();
        _ai = AiReadinessCheck.Check(index).ToList();
    }

    private void CheckEntity(EntityRecord entity)
    {
        var problems = _schema.Check(entity).ToList();
        if (problems.Count > 0) _byEntity[entity.Name] = problems;
        else _byEntity.Remove(entity.Name);
    }
}
