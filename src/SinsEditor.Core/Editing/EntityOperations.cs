using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Editing;

public enum DeleteMode
{
    /// <summary>Remove the entity from the manifest (and its file from the edited mod). The game no longer loads it.</summary>
    RemoveEntity,
    /// <summary>Delete only the edited mod's override, so the version from a lower layer is used again.</summary>
    RestoreLowerLayer,
}

/// <summary>
/// Plans structural changes: duplicate (also used to create new entities from a template), rename and delete.
/// Works on the saved state in the index; the caller makes sure there are no unsaved edits and reloads afterwards.
/// </summary>
public static class EntityOperations
{
    private const string GameInfo = "GameInfo/";
    private static readonly char[] InvalidNameChars = [.. Path.GetInvalidFileNameChars(), ' ', '"'];

    /// <summary>Why a name can't be used for a new entity, or null if it can.</summary>
    public static string? ValidateNewName(ModWorkspace workspace, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.IndexOfAny(InvalidNameChars) >= 0) return "Use letters, digits and underscores only, without spaces.";
        if (name.EndsWith(".entity", StringComparison.OrdinalIgnoreCase)) return "Leave out the .entity extension.";
        if (workspace.Index.Entities.ContainsKey(name) || workspace.Stack.Resolve(GameInfo + name + ".entity") != null)
            return "An entity with this name already exists.";
        return null;
    }

    public static ChangeSet PlanDuplicate(ModWorkspace workspace, EntityRecord source, string newName, bool addToRaces, bool copyStrings)
    {
        var p = new Planner(workspace, $"Duplicate {source.Name} as {newName}");
        string newPath = GameInfo + newName + ".entity";
        var copy = new EntityEditSession(source with { Name = newName, File = p.ModFile(newPath) });

        if (copyStrings)
        {
            foreach (var reference in workspace.Index.OutgoingReferences(source).Where(r => r.Kind == ReferenceKind.String && r.Resolved))
            {
                string newId = p.NewStringId(newName, reference.Key);
                p.AddString(newId, workspace.Index.Strings.Get(reference.Target) ?? "");
                copy.SetValue(reference.LineIndex, SinsValue.FormatString(newId));
            }
        }
        p.Describe(newPath, $"New entity, a copy of {source.Name}");
        p.Set.Set(new FileChange(newPath, copy.Document, ""));
        p.ManifestAdd(newName);

        if (addToRaces)
        {
            foreach (var item in workspace.Index.RaceItems.Where(i => string.Equals(i.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var race = p.Session(item.Race);
                // Every list entry naming the source gets a copy right after it, naming the new entity.
                var lines = race.Document.Lines;
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (lines[i].Key != "entityDefName" || !IsQuoted(lines[i].Value, source.Name)) continue;
                    if (CountedList.Find(race.Document, i) is not { } list) continue;
                    int index = list.IndexOfItemContaining(i);
                    if (index < 0) continue;
                    ListEdits.DuplicateItem(race, list, index);
                    race.SetValue(list.Items[index].End, SinsValue.FormatString(newName));
                }
                p.Describe(item.Race.File.RelativePath, $"Add {newName} next to {source.Name} in {item.Section}");
            }
        }
        return p.Finish();
    }

    public static ChangeSet PlanRename(ModWorkspace workspace, EntityRecord entity, string newName)
    {
        var p = new Planner(workspace, $"Rename {entity.Name} to {newName}");
        string newPath = GameInfo + newName + ".entity";
        var renamed = new EntityEditSession(entity with { Name = newName, File = p.ModFile(newPath) });

        foreach (var reference in workspace.Index.IncomingReferences(ReferenceKind.Entity, entity.Name))
        {
            if (reference.Source.Name == entity.Name)
            {
                renamed.SetValue(reference.LineIndex, SinsValue.FormatString(newName));
                continue;
            }
            p.Session(reference.Source).SetValue(reference.LineIndex, SinsValue.FormatString(newName));
            p.Describe(reference.Source.File.RelativePath, $"{reference.Key} → {newName}");
        }
        p.Describe(newPath, $"Renamed from {entity.Name}");
        p.Set.Set(new FileChange(newPath, renamed.Document, ""));
        if (p.ModHas(entity.File.RelativePath))
        {
            p.Describe(entity.File.RelativePath, "Delete (renamed)");
            p.Set.Set(new FileChange(entity.File.RelativePath, null, ""));
        }
        if (entity.InManifest) p.ManifestRename(entity.Name, newName);
        p.NoteUnrecognisedMentions(entity, "still use the old name");
        return p.Finish();
    }

    public static ChangeSet PlanDelete(ModWorkspace workspace, EntityRecord entity, DeleteMode mode, bool removeReferences)
    {
        var p = new Planner(workspace, $"Delete {entity.Name}");
        if (mode == DeleteMode.RestoreLowerLayer)
        {
            if (!entity.File.IsOverride || !p.ModHas(entity.File.RelativePath))
                throw new InvalidOperationException($"{entity.Name} doesn't override anything in a lower layer.");
            p.Describe(entity.File.RelativePath, $"Delete the override; {entity.File.OverriddenLayers[0].Name}'s version is used again");
            p.Set.Set(new FileChange(entity.File.RelativePath, null, ""));
            return p.Finish();
        }

        if (p.ModHas(entity.File.RelativePath))
        {
            p.Describe(entity.File.RelativePath, "Delete");
            p.Set.Set(new FileChange(entity.File.RelativePath, null, ""));
        }
        if (entity.InManifest) p.ManifestRemove(entity.Name);

        var references = workspace.Index.IncomingReferences(ReferenceKind.Entity, entity.Name).Where(r => r.Source.Name != entity.Name).ToList();
        if (removeReferences)
        {
            // Bottom-up per file, so removing a list item doesn't shift lines still to be handled.
            foreach (var reference in references.OrderByDescending(r => r.LineIndex))
            {
                var session = p.Session(reference.Source);
                var list = CountedList.Find(session.Document, reference.LineIndex);
                int index = list?.Items.ToList().FindIndex(i => i.Start == reference.LineIndex) ?? -1;
                // Only single-line items (like entityDefName in a race list) are removed; a reference inside a block is cleared.
                if (list != null && index >= 0 && list.Items[index].End == reference.LineIndex + 1)
                {
                    ListEdits.RemoveItem(session, list, index);
                    p.Describe(reference.Source.File.RelativePath, $"Remove {entity.Name} from {list.ItemKey} list");
                }
                else
                {
                    session.SetValue(reference.LineIndex, "\"\"");
                    p.Describe(reference.Source.File.RelativePath, $"Clear {reference.Key}");
                }
            }
        }
        else if (references.Count > 0)
        {
            p.Set.Notes.Add($"{references.Count} references in {references.Select(r => r.Source.Name).Distinct().Count()} entities will point to a missing entity.");
        }
        p.NoteUnrecognisedMentions(entity, "mention the deleted name");
        return p.Finish();
    }

    private static bool IsQuoted(string raw, string name) =>
        SinsValue.TryGetString(raw, out var value) && string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Collects working copies of every touched file and turns them into a <see cref="ChangeSet"/>.</summary>
    private sealed class Planner(ModWorkspace workspace, string title)
    {
        private readonly Dictionary<string, EntityEditSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _descriptions = new(StringComparer.OrdinalIgnoreCase);
        private SinsDocument? _manifest;
        private string? _manifestPath;
        private SinsDocument? _strings;
        private string? _stringsPath;
        private readonly HashSet<string> _newStringIds = new(StringComparer.OrdinalIgnoreCase);

        public ChangeSet Set { get; } = new(title);

        private Layer Mod => workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");

        public ResolvedFile ModFile(string relative) =>
            new(relative, Mod, Path.Combine(Mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar)), []);

        public bool ModHas(string relative) => Mod.Contains(relative);

        public EntityEditSession Session(EntityRecord entity)
        {
            if (!_sessions.TryGetValue(entity.Name, out var session)) _sessions[entity.Name] = session = new EntityEditSession(entity);
            return session;
        }

        public void Describe(string path, string text)
        {
            if (!_descriptions.TryGetValue(path, out var list)) _descriptions[path] = list = [];
            if (!list.Contains(text)) list.Add(text);
        }

        // ---- Manifest ----

        private SinsDocument Manifest()
        {
            if (_manifest != null) return _manifest;
            var resolved = workspace.Stack.Resolve("entity.manifest") ?? throw new InvalidOperationException("No entity.manifest found.");
            _manifestPath = Mod.Contains(resolved.RelativePath) ? resolved.RelativePath : "Entity.manifest";
            return _manifest = SinsDocument.Load(resolved.AbsolutePath);
        }

        public void ManifestAdd(string name)
        {
            var doc = Manifest();
            int last = doc.Lines.FindLastIndex(l => l.Key == "entityName");
            doc.InsertLine(last + 1, last >= 0 ? doc.Lines[last].Indent : "", "entityName", SinsValue.FormatString(name + ".entity"));
            AdjustManifestCount(+1);
            Describe(_manifestPath!, $"Add {name}");
        }

        public void ManifestRemove(string name)
        {
            var doc = Manifest();
            int line = FindManifestEntry(doc, name);
            if (line < 0) return;
            doc.RemoveLine(line);
            AdjustManifestCount(-1);
            Describe(_manifestPath!, $"Remove {name}");
        }

        public void ManifestRename(string oldName, string newName)
        {
            var doc = Manifest();
            int line = FindManifestEntry(doc, oldName);
            if (line < 0) { ManifestAdd(newName); return; }
            doc.Lines[line].SetRawValue(SinsValue.FormatString(newName + ".entity"));
            Describe(_manifestPath!, $"Rename {oldName} to {newName}");
        }

        private static int FindManifestEntry(SinsDocument doc, string name) =>
            doc.Lines.FindIndex(l => l.Key == "entityName" && IsQuoted(l.Value, name + ".entity"));

        private void AdjustManifestCount(int delta)
        {
            if (_manifest!.Lines.FirstOrDefault(l => l.Key == "entityNameCount") is { } count && SinsValue.TryGetInt(count.Value, out int n))
                count.SetRawValue(SinsValue.FormatInt(n + delta));
        }

        // ---- Strings ----

        public string NewStringId(string entityName, string key)
        {
            string suffix = key.ToUpperInvariant() switch
            {
                "NAMESTRINGID" => "NAME",
                "DESCRIPTIONSTRINGID" or "DESCSTRINGID" => "DESCRIPTION",
                var k => k.Replace("STRINGID", "").Replace("ID", "").Trim('_'),
            };
            string baseId = $"IDS_{entityName.ToUpperInvariant()}_{suffix}";
            string id = baseId;
            for (int n = 2; workspace.Index.Strings.Contains(id) || _newStringIds.Contains(id); n++) id = $"{baseId}_{n}";
            _newStringIds.Add(id);
            return id;
        }

        public void AddString(string id, string value)
        {
            if (_strings is null)
            {
                var table = workspace.Index.Strings;
                _strings = table.Document is { } d ? SinsDocument.Parse(d.ToBytes()) : SinsDocument.Parse("TXT\r\nNumStrings 0\r\n"u8.ToArray());
                _stringsPath = Languages.StringFilePath(Mod, table.Language);
            }
            int end = _strings.Lines.Count;
            _strings.InsertLine(end, "", "StringInfo");
            _strings.InsertLine(end + 1, "\t", "ID", SinsValue.FormatString(id));
            _strings.InsertLine(end + 2, "\t", "Value", SinsValue.FormatString(value));
            if (_strings.Lines.FirstOrDefault(l => l.Key == "NumStrings") is { } count && SinsValue.TryGetInt(count.Value, out int n))
                count.SetRawValue(SinsValue.FormatInt(n + 1));
            Describe(_stringsPath!, $"Add {id}");
        }

        // ---- Safety net ----

        /// <summary>Notes entities that quote the name in a property the reference detection doesn't know.</summary>
        public void NoteUnrecognisedMentions(EntityRecord entity, string what)
        {
            var known = workspace.Index.IncomingReferences(ReferenceKind.Entity, entity.Name).Select(r => (r.Source.Name, r.LineIndex)).ToHashSet();
            var others = new List<string>();
            foreach (var other in workspace.Index.ActiveEntities)
            {
                if (other.Name == entity.Name) continue;
                var lines = other.Document.Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (IsQuoted(lines[i].Value, entity.Name) && !known.Contains((other.Name, i)))
                        others.Add($"{other.Name}:{i + 1} ({lines[i].Key})");
                }
            }
            if (others.Count > 0)
                Set.Notes.Add($"{others.Count} lines {what} in properties the editor doesn't treat as references: " +
                              string.Join(", ", others.Take(10)) + (others.Count > 10 ? ", …" : ""));
        }

        public ChangeSet Finish()
        {
            foreach (var session in _sessions.Values.Where(s => s.IsDirty))
                Set.Set(new FileChange(session.Original.File.RelativePath, session.Document, ""));
            if (_manifest != null) Set.Set(new FileChange(_manifestPath!, _manifest, ""));
            if (_strings != null) Set.Set(new FileChange(_stringsPath!, _strings, ""));

            // Attach the collected descriptions to the changes.
            foreach (var change in Set.Changes.ToList())
            {
                string text = _descriptions.TryGetValue(change.RelativePath, out var d) ? string.Join("; ", d) : "Update";
                Set.Set(change with { Description = text });
            }
            return Set;
        }
    }
}
