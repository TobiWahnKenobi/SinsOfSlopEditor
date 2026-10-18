using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Validation;

/// <summary>
/// What the game's AI needs to play a race, and doesn't get. The AI follows fixed build orders: it looks for ships and
/// planet modules by their role (<c>frigateRoleType</c>, <c>planetModuleRoleType</c>) in the race's lists, and researches
/// its way to them. When one is missing, or behind research it can't reach, it stalls or never expands. Reported as
/// warnings for every selectable race (<c>isSelectable TRUE</c>); the base game's races pass all of them.
/// </summary>
public static class AiReadinessCheck
{
    /// <summary>A role the AI needs: where it's listed, the role, whether it's needed from the start, and why.</summary>
    private sealed record Need(string Section, string RoleKey, string[] Roles, bool AtStart, string What, string Without);

    /// <summary>
    /// Planet types the base game locks behind research (its subjects with <c>PlanetIsColonizable</c>). Mods may add
    /// such modifiers for other types (Terran, Desert …), but whether those are locked can't be told from the data, so
    /// only these are checked.
    /// </summary>
    private static readonly HashSet<string> ResearchLockedPlanetTypes = new(["Ice", "Volcanic", "Greenhouse", "Iron", "Oceanic"], StringComparer.OrdinalIgnoreCase);

    private static readonly Need[] Needs =
    [
        new("frigateInfo", "frigateRoleType", ["Colony"], true, "colony frigate (frigateRoleType Colony)",
            "the AI can't colonise planets and stays on its home planet"),
        new("frigateInfo", "frigateRoleType", ["Scout"], true, "scout frigate (frigateRoleType Scout)",
            "the AI doesn't explore, so it finds no planets to colonise"),
        new("frigateInfo", "frigateRoleType", ["ModuleConstructor"], true, "constructor frigate (frigateRoleType ModuleConstructor)",
            "the AI can't build any planet module: no factories, extractors or labs"),
        new("frigateInfo", "frigateRoleType", ["Light", "Heavy", "Lrm", "Siege", "AntiFighter", "Carrier", "Corvette"], true,
            "combat frigate (frigateRoleType Light, Heavy, Lrm, …)", "the AI has nothing to fight or to clear neutral defences with"),
        new("capitalShipInfo", "", [], false, "capital ship", "the AI's fleets have no capital ships, and it can't use its first free one"),
        new("planetModuleInfo", "planetModuleRoleType", ["FRIGATEFACTORY"], true, "frigate factory (planetModuleRoleType FRIGATEFACTORY)",
            "the AI can't build frigates, including colony and constructor frigates"),
        new("planetModuleInfo", "planetModuleRoleType", ["CAPITALSHIPFACTORY"], false, "capital ship factory (planetModuleRoleType CAPITALSHIPFACTORY)",
            "the AI can't build capital ships"),
        new("planetModuleInfo", "planetModuleRoleType", ["METALEXTRACTOR"], true, "metal extractor (planetModuleRoleType METALEXTRACTOR)",
            "the AI runs out of metal"),
        new("planetModuleInfo", "planetModuleRoleType", ["CRYSTALEXTRACTOR"], true, "crystal extractor (planetModuleRoleType CRYSTALEXTRACTOR)",
            "the AI runs out of crystal"),
        new("planetModuleInfo", "planetModuleRoleType", ["RESEARCHCOMBAT"], false, "military research lab (planetModuleRoleType RESEARCHCOMBAT)",
            "the AI can't research higher military tiers"),
        new("planetModuleInfo", "planetModuleRoleType", ["RESEARCHNONCOMBAT"], false, "civic research lab (planetModuleRoleType RESEARCHNONCOMBAT)",
            "the AI can't research higher civic tiers, including colonisation of most planet types"),
    ];

    public static IEnumerable<Problem> Check(ModIndex index)
    {
        // Planet types the game only lets races colonise after research, and the subjects in the mod that unlock them.
        var colonisationResearch = index.ActiveEntities
            .Where(e => e.EntityType == "ResearchSubject")
            .SelectMany(e => ColonisesPlanetTypes(e.Document).Select(type => (Type: type, Subject: e.Name)))
            .Where(x => ResearchLockedPlanetTypes.Contains(x.Type))
            .ToLookup(x => x.Type, x => x.Subject, StringComparer.OrdinalIgnoreCase);

        foreach (var race in index.Races)
        {
            if (!IsTrue(TopLevel(race.Document, "isSelectable"))) continue;
            string raceName = index.DisplayName(race) ?? race.Name;
            var items = index.ItemsOf(race).ToList();
            var research = items.Where(i => i.Section == "researchInfo").Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachability = new Reachability(index, research);

            foreach (var need in Needs)
            {
                var candidates = items
                    .Where(i => i.Section == need.Section && i.Entity != null)
                    .Where(i => need.Roles.Length == 0 || need.Roles.Contains(TopLevel(i.Entity!.Document, need.RoleKey), StringComparer.OrdinalIgnoreCase))
                    .Select(i => i.Entity!)
                    .DistinctBy(e => e.Name)
                    .ToList();
                if (candidates.Count == 0)
                {
                    yield return Warn(race, $"{raceName} has no {need.What}: {need.Without}.");
                    continue;
                }
                if (candidates.Any(e => Prerequisites(e.Document).Count == 0)) continue;
                var reachable = candidates.Where(e => reachability.CanReach(e)).ToList();
                if (reachable.Count == 0)
                {
                    var blockers = candidates.SelectMany(e => reachability.Blockers(e)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4);
                    yield return Warn(race, $"{raceName} can never get {A(need.What)}: it needs research that isn't on the race's research list " +
                                            $"or can't be reached ({string.Join(", ", blockers)}). So {need.Without}.");
                }
                else if (need.AtStart)
                {
                    yield return Warn(race, $"{raceName} only gets {A(need.What)} after research ({string.Join(", ", Prerequisites(reachable[0].Document).Take(3))}). " +
                                            $"The AI expects it from the start; until then {need.Without}.");
                }
            }

            foreach (var group in colonisationResearch)
            {
                if (group.Any(subject => research.Contains(subject) && reachability.CanReachSubject(subject))) continue;
                bool listed = group.Any(research.Contains);
                yield return Warn(race, listed
                    ? $"{raceName} can't reach the research to colonise {group.Key} planets ({string.Join(", ", group.Where(research.Contains).Take(3))}): the AI never colonises them."
                    : $"{raceName} has no research to colonise {group.Key} planets (other races use {string.Join(", ", group.Take(2))}): the AI never colonises them.");
            }
        }
    }

    private static string A(string what) => ("aeiou".Contains(char.ToLowerInvariant(what[0])) ? "an " : "a ") + what;

    private static Problem Warn(EntityRecord race, string message) =>
        new(ProblemSeverity.Warning, ProblemCodes.AiReadiness, message, race.File.RelativePath);

    /// <summary>Which research subjects a race can get: on its list, with every prerequisite gettable too (no cycles).</summary>
    private sealed class Reachability(ModIndex index, HashSet<string> research)
    {
        private readonly Dictionary<string, bool> _known = new(StringComparer.OrdinalIgnoreCase);

        public bool CanReach(EntityRecord entity) => Prerequisites(entity.Document).All(CanReachSubject);

        public bool CanReachSubject(string subject)
        {
            if (_known.TryGetValue(subject, out bool known)) return known;
            // Assume not while checking it, so a cycle counts as unreachable.
            _known[subject] = false;
            bool result = research.Contains(subject) && index.Entities.TryGetValue(subject, out var entity) && entity.InManifest
                          && Prerequisites(entity.Document).All(CanReachSubject);
            return _known[subject] = result;
        }

        /// <summary>The first subjects on the way that can't be had: not listed, missing, or in a cycle.</summary>
        public IEnumerable<string> Blockers(EntityRecord entity)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(Prerequisites(entity.Document));
            while (queue.TryDequeue(out var subject))
            {
                if (!seen.Add(subject) || CanReachSubject(subject)) continue;
                if (!research.Contains(subject) || !index.Entities.TryGetValue(subject, out var e) || !e.InManifest)
                {
                    yield return subject;
                    continue;
                }
                var next = Prerequisites(e.Document).Where(p => !CanReachSubject(p)).ToList();
                if (next.Count == 0 || next.All(seen.Contains)) yield return subject;
                foreach (var p in next) queue.Enqueue(p);
            }
        }
    }

    /// <summary>The research subjects in the entity's top-level <c>Prerequisites</c> block.</summary>
    internal static List<string> Prerequisites(SinsDocument doc)
    {
        var result = new List<string>();
        var lines = doc.Lines;
        int start = lines.FindIndex(l => l.Depth == 0 && l.Key.Equals("Prerequisites", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return result;
        for (int i = start + 1; i < lines.Count && (lines[i].IsBlank || lines[i].Depth > 0); i++)
        {
            if (lines[i].Key == "Subject" && SinsValue.TryGetString(lines[i].Value, out var subject) && subject.Length > 0) result.Add(subject);
        }
        return result;
    }

    private static IEnumerable<string> ColonisesPlanetTypes(SinsDocument doc)
    {
        var lines = doc.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Key != "modifierType" || !SinsValue.TryGetString(lines[i].Value, out var type) || type != "PlanetIsColonizable") continue;
            for (int j = i + 1; j < lines.Count && j <= i + 3; j++)
                if (lines[j].Key == "linkedPlanetType" && SinsValue.TryGetString(lines[j].Value, out var planet) && planet.Length > 0) yield return planet;
        }
    }

    private static string TopLevel(SinsDocument doc, string key) =>
        doc.Lines.FirstOrDefault(l => l.Depth == 0 && l.Key == key) is { } line
            ? SinsValue.TryGetString(line.Value, out var s) ? s : line.Value
            : "";

    private static bool IsTrue(string value) => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
}
