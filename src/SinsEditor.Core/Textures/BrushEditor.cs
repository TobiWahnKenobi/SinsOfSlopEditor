using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Textures;

/// <summary>
/// What a brush should become. <paramref name="Images"/> holds every image of the brush, keyed by state
/// (<see cref="Brush.SimpleState"/> for Simple brushes) — callers pass existing images they don't replace, too.
/// All images go into <paramref name="Texture"/>, unless <paramref name="TextureByState"/> names a texture per state:
/// the game's icon sheets keep each state in its own atlas (…_normal, …_pressed, …_highlight, …_disabled) at the same place.
/// </summary>
public sealed record BrushEditRequest(
    string BrushName,
    bool IsNew,
    IReadOnlyDictionary<string, PixelImage> Images,
    string Texture,
    bool NewTexture,
    TextureFileFormat NewTextureFormat = TextureFileFormat.Tga32,
    string? BrushesFile = null,
    IReadOnlyDictionary<string, string>? TextureByState = null);

public enum AtlasMode
{
    /// <summary>The new images have the old sizes: they replace the old pixels where they are; nothing else changes.</summary>
    InPlace,
    /// <summary>Every image in the atlas belongs to a brush that gets written: everything is packed anew.</summary>
    Repack,
    /// <summary>Brushes that can't be rewritten (lower layers) use the atlas: their regions stay, new images go into free space.</summary>
    Append,
}

public sealed record BrushEditPlan(ChangeSet Changes, AtlasMode Mode, int AtlasWidth, int AtlasHeight, int MovedRegions, PixelImage Atlas);

/// <summary>
/// Plans brush edits: atlas packing, texture writing and every <c>.brushes</c> change that follows.
/// Works on an atlas set: one texture, or the per-state textures of an icon sheet, which share one layout —
/// a brush occupies the same region in every texture of the set.
/// </summary>
public static class BrushEditor
{
    private const string BrushManifest = "brush.manifest";

    public static string? ValidateNewName(ModWorkspace workspace, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.Any(c => char.IsWhiteSpace(c) || c == '"')) return "Use letters, digits and underscores, without spaces.";
        if (workspace.Index.Brushes.Contains(name)) return "A brush with this name already exists.";
        return null;
    }

    public static string? ValidateNewTexture(ModWorkspace workspace, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a texture name.";
        if (name.IndexOfAny([.. Path.GetInvalidFileNameChars(), ' ']) >= 0) return "Use letters, digits and underscores, without spaces.";
        if (workspace.Index.Textures.ContainsKey(name)) return "A texture with this name already exists.";
        return null;
    }

    /// <summary>
    /// One place in the layout: a brush's images that share a region, at most one per texture of the set.
    /// <see cref="States"/> lists, per texture, the brush states that point there (Normal and Focused often share).
    /// </summary>
    private sealed class Slot(string key, string brush, string file, PixelBox? oldBox, bool isTarget)
    {
        public string Key { get; } = key;
        public string Brush { get; } = brush;
        public string File { get; } = file;
        public PixelBox? OldBox { get; } = oldBox;
        public bool IsTarget { get; } = isTarget;
        public Dictionary<string, PixelImage?> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Width => OldBox?.Width ?? Images.Values.First(i => i != null)!.Width;
        public int Height => OldBox?.Height ?? Images.Values.First(i => i != null)!.Height;
    }

    public static async Task<BrushEditPlan> PlanAsync(ModWorkspace workspace, BrushEditRequest request, CancellationToken ct = default)
    {
        var index = workspace.Index;
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        string Norm(string t) => Path.GetFileNameWithoutExtension(t);

        var textureOf = request.Images.Keys.ToDictionary(s => s,
            s => Norm(request.TextureByState?.GetValueOrDefault(s) ?? request.Texture), StringComparer.OrdinalIgnoreCase);
        var set = textureOf.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (request.NewTexture && set.Count > 1) throw new InvalidOperationException("A new texture holds all states of the brush.");

        var resolved = new Dictionary<string, ResolvedFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (string t in set)
        {
            resolved[t] = request.NewTexture ? null : index.Textures.GetValueOrDefault(t)
                ?? throw new InvalidOperationException($"Texture '{t}' doesn't exist.");
        }

        NamedEntry? targetEntry = request.IsNew ? null : index.Brushes.Entries.GetValueOrDefault(request.BrushName)
            ?? throw new InvalidOperationException($"Brush '{request.BrushName}' doesn't exist.");
        string brushesFile = targetEntry?.File.RelativePath
            ?? request.BrushesFile ?? throw new InvalidOperationException("Choose a .brushes file for the new brush.");
        // Files that will be written: the mod's own brush files, plus the edited brush's file (copied in if it's lower).
        bool Writable(string file) => mod.Contains(file) || string.Equals(file, brushesFile, StringComparison.OrdinalIgnoreCase);

        // Formats and current pixels of the set.
        bool needsAlpha = request.Images.Values.Any(TextureFormats.HasTransparency);
        var formats = new Dictionary<string, TextureFileFormat>(StringComparer.OrdinalIgnoreCase);
        var oldAtlases = new Dictionary<string, PixelImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, file) in resolved)
        {
            if (file is null)
            {
                formats[t] = request.NewTextureFormat;
                continue;
            }
            formats[t] = TextureFormats.ForRewrite(TextureFormats.Detect(file.AbsolutePath)
                ?? throw new InvalidOperationException($"'{Path.GetFileName(file.RelativePath)}' has a format the editor can't write back."), needsAlpha);
            oldAtlases[t] = TextureLoader.Load(file.AbsolutePath);
        }
        bool dds = formats.Values.Any(TextureFormats.IsDds);
        int padding = dds ? 4 : 2, align = dds ? 4 : 1;

        // The edited brush. Per texture, states with identical pixels share a region (Normal and Focused often do);
        // the i-th distinct image of every texture shares slot i, so an icon sheet's states line up across its textures.
        var slots = new List<Slot>();
        foreach (var byTexture in request.Images.GroupBy(kv => textureOf[kv.Key], StringComparer.OrdinalIgnoreCase))
        {
            int i = 0;
            foreach (var same in byTexture.GroupBy(kv => ImageImport.Hash(kv.Value)))
            {
                if (slots.Count <= i) slots.Add(new Slot($"target:{i}", request.BrushName, brushesFile, null, isTarget: true));
                var slot = slots[i++];
                slot.Images[byTexture.Key] = same.First().Value;
                slot.States[byTexture.Key] = [.. same.Select(kv => kv.Key)];
            }
        }
        // Slots of one brush share a region, so their images need one size.
        foreach (var slot in slots)
        {
            if (slot.Images.Values.Select(img => (img!.Width, img.Height)).Distinct().Count() > 1)
                throw new InvalidOperationException("All states of the brush need the same size.");
        }

        // Everything else in the set, grouped by brush and region.
        var others = new Dictionary<string, Slot>();
        foreach (var entry in index.Brushes.Entries.Values)
        {
            if (string.Equals(entry.Name, request.BrushName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (state, image) in Brush.FromEntry(entry).Images)
            {
                string t = Norm(image.FileName);
                if (image.Box.Width <= 0 || image.Box.Height <= 0 || !resolved.ContainsKey(t)) continue;
                string key = $"{entry.Name}@{image.Box}";
                if (!others.TryGetValue(key, out var slot)) others[key] = slot = new Slot(key, entry.Name, entry.File.RelativePath, image.Box, isTarget: false);
                if (!slot.States.TryGetValue(t, out var states)) slot.States[t] = states = [];
                states.Add(state);
                slot.Images.TryAdd(t, null);
            }
        }

        var inPlace = InPlaceBoxes(targetEntry, slots, resolved, Norm);
        var mode = inPlace != null ? AtlasMode.InPlace : others.Values.All(s => Writable(s.File)) ? AtlasMode.Repack : AtlasMode.Append;
        if (mode == AtlasMode.Repack)
        {
            foreach (var slot in others.Values)
            {
                foreach (string t in slot.Images.Keys.ToList()) slot.Images[t] = oldAtlases[t].Crop(slot.OldBox!.Value);
            }
            slots.AddRange(others.Values);
        }

        int minW = mode != AtlasMode.Repack ? oldAtlases.Values.Select(a => a.Width).DefaultIfEmpty(1).Max() : 1;
        int minH = mode != AtlasMode.Repack ? oldAtlases.Values.Select(a => a.Height).DefaultIfEmpty(1).Max() : 1;
        var fixedRegions = mode == AtlasMode.Append ? others.Values.Select(s => s.OldBox!.Value).Distinct().ToList() : [];
        var pack = inPlace != null ? new PackResult(minW, minH, inPlace) : AtlasPacker.Pack(slots.Select(s => new PackItem(s.Key, s.Width, s.Height)).ToList(), padding, align, fixedRegions, minW, minH)
            ?? throw new InvalidOperationException("The images don't fit into a 4096×4096 texture. Use a new texture.");

        var changes = new ChangeSet(request.IsNew ? $"New brush {request.BrushName}" : $"Edit brush {request.BrushName}");
        var atlases = new Dictionary<string, PixelImage>(StringComparer.OrdinalIgnoreCase);
        foreach (string t in set)
        {
            var placements = slots.Where(s => s.Images.TryGetValue(t, out var img) && img != null)
                .Select(s => (s.Images[t]!, pack.Boxes[s.Key]));
            var old = mode == AtlasMode.Repack ? null : oldAtlases.GetValueOrDefault(t);
            // In place, each texture keeps its own size and the neighbours' padding stays untouched.
            var atlas = AtlasPacker.Compose(mode == AtlasMode.InPlace ? old!.Width : pack.Width, mode == AtlasMode.InPlace ? old!.Height : pack.Height,
                old, placements, mode == AtlasMode.InPlace ? 0 : padding);
            atlases[t] = atlas;
            var file = resolved[t];
            string path = file?.RelativePath ?? $"Textures/{t}{TextureFormats.Extension(formats[t])}";
            changes.Set(new FileChange(path, null,
                $"{(file is null ? "New" : mode switch { AtlasMode.Repack => "Repacked", AtlasMode.Append => "Added to", _ => "Replaced images in" })} {atlas.Width}×{atlas.Height} {formats[t]}",
                TextureFormats.Encode(atlas, formats[t])));
        }

        // .brushes files.
        var docs = new Dictionary<string, SinsDocument>(StringComparer.OrdinalIgnoreCase);
        async Task<SinsDocument> Doc(string file)
        {
            if (docs.TryGetValue(file, out var d)) return d;
            var found = workspace.Stack.Resolve(file);
            d = found is null
                ? SinsDocument.Parse("TXT\r\nonlyLoadOnDemand FALSE\r\nbrushCount 0\r\n"u8.ToArray())
                : SinsDocument.Load(await workspace.Cache.GetTextPathAsync(found.AbsolutePath, ct));
            return docs[file] = d;
        }

        int moved = 0;
        if (mode == AtlasMode.Repack)
        {
            foreach (var slot in others.Values)
            {
                var box = pack.Boxes[slot.Key];
                if (box == slot.OldBox) continue;
                var doc = await Doc(slot.File);
                foreach (var (t, states) in slot.States)
                    foreach (string state in states) SetImage(doc, slot.Brush, state, t, box);
                moved++;
            }
        }

        // In place, the brush keeps its regions and its definition stays as it is.
        var target = mode == AtlasMode.InPlace ? null : await Doc(brushesFile);
        var newImages = new Dictionary<string, (string Texture, PixelBox Box)>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots.Where(s => s.IsTarget))
            foreach (var (t, states) in slot.States)
                foreach (string state in states) newImages[state] = (t, pack.Boxes[slot.Key]);
        if (target != null) WriteBrush(target, request.BrushName, newImages);
        foreach (var (file, doc) in docs)
            changes.Set(new FileChange(file, doc, file.Equals(brushesFile, StringComparison.OrdinalIgnoreCase)
                ? (request.IsNew ? $"Add brush {request.BrushName}" : $"Update brush {request.BrushName}")
                : "New positions for brushes in the repacked atlas"));

        if (workspace.Stack.Resolve(brushesFile) is null)
        {
            var manifestFile = workspace.Stack.Resolve(BrushManifest) ?? throw new InvalidOperationException("No brush.manifest found.");
            var manifest = SinsDocument.Load(manifestFile.AbsolutePath);
            int last = manifest.Lines.FindLastIndex(l => l.Key == "brushFile");
            manifest.InsertLine(last + 1, last >= 0 ? manifest.Lines[last].Indent : "", "brushFile", SinsValue.FormatString(Path.GetFileName(brushesFile)));
            if (manifest.Lines.FirstOrDefault(l => l.Key == "brushFileCount") is { } count && SinsValue.TryGetInt(count.Value, out int n))
                count.SetRawValue(SinsValue.FormatInt(n + 1));
            changes.Set(new FileChange(mod.Contains(manifestFile.RelativePath) ? manifestFile.RelativePath : "brush.manifest", manifest,
                $"Add {Path.GetFileName(brushesFile)}"));
        }

        if (mode != AtlasMode.Repack && resolved.Values.Any(f => f != null && !mod.Contains(f.RelativePath)))
            changes.Notes.Add($"{string.Join(", ", set)} {(set.Count == 1 ? "comes" : "come")} from a lower layer. The mod gets its own copy with the new " +
                              "images added; every existing image stays where it was, so brushes elsewhere keep working.");
        if (set.Count > 1)
            changes.Notes.Add($"The brush keeps one texture per state ({string.Join(", ", set)}); all of them get the same layout.");
        if (pack.Width > 2048 || pack.Height > 2048)
            changes.Notes.Add($"The atlas is {pack.Width}×{pack.Height}. Very large textures use a lot of video memory.");

        string previewTexture = textureOf.GetValueOrDefault(BrushStates.Normal) ?? textureOf.GetValueOrDefault(Brush.SimpleState) ?? set[0];
        return new BrushEditPlan(changes, mode, pack.Width, pack.Height, moved, atlases[previewTexture]);
    }

    /// <summary>
    /// The regions the edited brush has now, if every slot fits its old one exactly: same size, same texture for all of its
    /// states, and no two slots sharing a region. Null otherwise (new brush, new texture, other size).
    /// </summary>
    private static Dictionary<string, PixelBox>? InPlaceBoxes(NamedEntry? target, List<Slot> slots,
        Dictionary<string, ResolvedFile?> textures, Func<string, string> norm)
    {
        if (target is null || textures.Values.Any(f => f is null)) return null;
        var old = Brush.FromEntry(target).Images;
        var boxes = new Dictionary<string, PixelBox>();
        var used = new HashSet<(string, PixelBox)>();
        foreach (var slot in slots.Where(s => s.IsTarget))
        {
            PixelBox? box = null;
            foreach (var (t, states) in slot.States)
            {
                foreach (string state in states)
                {
                    if (!old.TryGetValue(state, out var image) || !string.Equals(norm(image.FileName), t, StringComparison.OrdinalIgnoreCase)) return null;
                    if (box != null && box != image.Box) return null;
                    box = image.Box;
                }
                if (!used.Add((t.ToLowerInvariant(), box!.Value))) return null;
            }
            if (box is not { } b || b.Width != slot.Width || b.Height != slot.Height) return null;
            boxes[slot.Key] = b;
        }
        return boxes;
    }

    private static string Box(PixelBox b) => $"[ {b.X} , {b.Y} , {b.Width} , {b.Height} ]";

    /// <summary>Points one image of a brush (a state, or the brush itself for Simple) at a new region.</summary>
    private static void SetImage(SinsDocument doc, string brush, string state, string texture, PixelBox box)
    {
        var node = FindBrush(doc, brush) ?? throw new InvalidOperationException($"Brush '{brush}' wasn't found in its file.");
        var holder = state.Length == 0 ? node : node.Child(state) ?? throw new InvalidOperationException($"Brush '{brush}' has no {state} image.");
        holder.Child("fileName")?.Line!.SetRawValue(SinsValue.FormatString(texture));
        holder.Child("pixelBox")?.Line!.SetRawValue(Box(box));
    }

    private static SinsNode? FindBrush(SinsDocument doc, string brush) =>
        doc.BuildTree().Children.FirstOrDefault(n => n.Key == "brush" && n.Child("name") is { } name
            && SinsValue.TryGetString(name.Value, out var s) && string.Equals(s, brush, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the edited brush as a whole block (name, content, kept margin blocks, images), replacing the old one in place,
    /// or appending it and raising <c>brushCount</c>.
    /// </summary>
    private static void WriteBrush(SinsDocument doc, string brush, IReadOnlyDictionary<string, (string Texture, PixelBox Box)> images)
    {
        string e = doc.DefaultEnding;
        var existing = FindBrush(doc, brush);
        bool states = !images.ContainsKey(Brush.SimpleState);
        string content = existing?.Child("content") is { } c && SinsValue.TryGetString(c.Value, out var oldContent)
            ? (states ? (oldContent.StartsWith("Margins") ? "MarginsAndStates" : "States") : (oldContent.StartsWith("Margins") ? "Margins" : "Simple"))
            : (states ? "States" : "Simple");

        var lines = new List<SinsLine> { SinsLine.Create("", "brush", "", e), SinsLine.Create("\t", "name", SinsValue.FormatString(brush), e),
            SinsLine.Create("\t", "content", SinsValue.FormatString(content), e) };
        if (existing != null)
        {
            foreach (var margins in existing.Children.Where(n => n.Key is "stretchMargins" or "contentMargins"))
            {
                int end = CountedList.BlockEnd(doc, margins.LineIndex);
                lines.AddRange(doc.Lines.GetRange(margins.LineIndex, end - margins.LineIndex).Select(EntityEditSession.Clone));
            }
        }
        if (!states)
        {
            var (texture, box) = images[Brush.SimpleState];
            lines.Add(SinsLine.Create("\t", "fileName", SinsValue.FormatString(texture), e));
            lines.Add(SinsLine.Create("\t", "pixelBox", Box(box), e));
        }
        else
        {
            foreach (string state in BrushStates.All.Where(images.ContainsKey).Concat(images.Keys.Where(k => !BrushStates.All.Contains(k))))
            {
                var (texture, box) = images[state];
                lines.Add(SinsLine.Create("\t", state, "", e));
                lines.Add(SinsLine.Create("\t\t", "fileName", SinsValue.FormatString(texture), e));
                lines.Add(SinsLine.Create("\t\t", "pixelBox", Box(box), e));
            }
        }

        if (existing != null)
        {
            int end = CountedList.BlockEnd(doc, existing.LineIndex);
            doc.Lines.RemoveRange(existing.LineIndex, end - existing.LineIndex);
            doc.Lines.InsertRange(existing.LineIndex, lines);
            return;
        }
        if (doc.Lines.Count > 0 && doc.Lines[^1].Ending.Length == 0) doc.Lines[^1].Ending = e;
        doc.Lines.AddRange(lines);
        if (doc.Lines.FirstOrDefault(l => l.Key == "brushCount") is { } count && SinsValue.TryGetInt(count.Value, out int n))
            count.SetRawValue(SinsValue.FormatInt(n + 1));
    }
}
