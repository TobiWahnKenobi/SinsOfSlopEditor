namespace SinsEditor.Core.Index;

/// <summary>A group of entity types shown as one section in the editor, e.g. "Planet modules".</summary>
public sealed record EntityCategory(string Id, string Name, IReadOnlyList<string> EntityTypes, bool MatchPrefix = false)
{
    public bool Contains(string entityType) => MatchPrefix
        ? EntityTypes.Any(t => entityType.StartsWith(t, StringComparison.Ordinal))
        : EntityTypes.Contains(entityType);
}

public static class EntityCategories
{
    public static readonly EntityCategory Races = new("races", "Races", ["Player"]);
    public static readonly EntityCategory CapitalShips = new("capital-ships", "Capital ships", ["CapitalShip"]);
    public static readonly EntityCategory Titans = new("titans", "Titans", ["Titan"]);
    public static readonly EntityCategory Frigates = new("frigates", "Frigates", ["Frigate"]);
    public static readonly EntityCategory Strikecraft = new("strikecraft", "Strikecraft", ["Squad", "Fighter"]);
    public static readonly EntityCategory Starbases = new("starbases", "Starbases", ["StarBase"]);
    public static readonly EntityCategory PlanetModules = new("planet-modules", "Planet modules", ["PlanetModule"], MatchPrefix: true);
    public static readonly EntityCategory Upgrades = new("upgrades", "Upgrades", ["StarBaseUpgrade", "TitanUpgrade"]);
    public static readonly EntityCategory Abilities = new("abilities", "Abilities", ["Ability"]);
    public static readonly EntityCategory Buffs = new("buffs", "Buffs", ["Buff"]);
    public static readonly EntityCategory Research = new("research", "Research", ["ResearchSubject"]);
    public static readonly EntityCategory Planets = new("planets", "Planets", ["Planet", "PlanetBonus", "Star"]);

    public static readonly IReadOnlyList<EntityCategory> All =
        [Races, CapitalShips, Titans, Frigates, Strikecraft, Starbases, PlanetModules, Upgrades, Abilities, Buffs, Research, Planets];

    /// <summary>Category for everything not covered by <see cref="All"/>.</summary>
    public static readonly EntityCategory Other = new("other", "Other entities", []);

    public static EntityCategory For(string entityType) => All.FirstOrDefault(c => c.Contains(entityType)) ?? Other;

    public static EntityCategory? ById(string id) =>
        id == Other.Id ? Other : All.FirstOrDefault(c => c.Id == id);

    public static IEnumerable<EntityRecord> EntitiesIn(this ModIndex index, EntityCategory category) =>
        index.ActiveEntities.Where(e => For(e.EntityType) == category);
}
