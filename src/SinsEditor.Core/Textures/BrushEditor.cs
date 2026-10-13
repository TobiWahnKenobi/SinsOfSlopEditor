using SinsEditor.Core.Conversion;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Textures;

/// <summary>
/// What a brush should become. <paramref name="Images"/> holds every image of the brush, keyed by state
/// (<see cref="Brush.SimpleState"/> for Simple brushes) — callers pass existing images they don't replace, too.
/// </summary>
public sealed record BrushEditRequest(
    string BrushName,
    bool IsNew,
    IReadOnlyDictionary<string, PixelImage> Images,
    string Texture,
    bool NewTexture,
    TextureFileFormat NewTextureFormat = TextureFileFormat.Tga32,
    string? BrushesFile = null);

public enum AtlasMode
{
    /// <summary>Every image in the atlas belongs to a brush that gets written: everything is packed anew.</summary>
    Repack,
    /// <summary>Brushes that can't be rewritten (lower layers) use the atlas: their regions stay, new images go into free space.</summary>
    Append,
}

public sealed record BrushEditPlan(ChangeSet Changes, AtlasMode Mode, int AtlasWidth, int AtlasHeight, int MovedRegions, PixelImage Atlas);

/// <summary>Plans brush edits: atlas packing, texture writing and every <c>.brushes</c> change that follows.</summary>
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

    private sealed record Region(string Brush, string State, PixelBox Box, string File);

    public static async Task<BrushEditPlan> PlanAsync(ModWorkspace workspace, BrushEditRequest request, CancellationToken ct = default)
    {
        var index = workspace.Index;
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        string texture = Path.GetFileNameWithoutExtension(request.Texture);
        var existingTexture = request.NewTexture ? null : index.Textures.GetValueOrDefault(texture)
            ?? throw new InvalidOperationException($"Texture '{texture}' doesn't exist.");
        NamedEntry? targetEntry = request.IsNew ? null : index.Brushes.Entries.GetValueOrDefault(request.BrushName)
            ?? throw new InvalidOperationException($"Brush '{request.BrushName}' doesn't exist.");
        string brushesFile = targetEntry?.File.RelativePath
            ?? request.BrushesFile ?? throw new InvalidOperationException("Choose a .brushes file for the new brush.");

        // Files that will be written: the mod's own brush files, plus the edited brush's file (copied in if it's lower).
        bool Writable(string file) => mod.Contains(file) || string.Equals(file, brushesFile, StringComparison.OrdinalIgnoreCase);

        // Everything else stored in the atlas.
        var regions = new List<Region>();
        if (existingTexture != null)
        {
            foreach (var entry in index.Brushes.Entries.Values)
            {
                if (string.Equals(entry.Name, request.BrushName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (state, image) in Brush.FromEntry(entry).Images)
                {
                    if (image.Box.Width > 0 && image.Box.Height > 0 && SameTexture(image.FileName, texture))
                        regions.Add(new Region(entry.Name, state, image.Box, entry.File.RelativePath));
                }
            }
        }

        TextureFileFormat format;
        PixelImage? oldAtlas = null;
        if (existingTexture is null) format = request.NewTextureFormat;
        else
        {
            format = TextureFormats.Detect(existingTexture.AbsolutePath)
                ?? throw new InvalidOperationException($"'{Path.GetFileName(existingTexture.RelativePath)}' has a format the editor can't write back.");
            oldAtlas = TextureLoader.Load(existingTexture.AbsolutePath);
        }
        bool needsAlpha = request.Images.Values.Any(TextureFormats.HasTransparency);
        format = TextureFormats.ForRewrite(format, needsAlpha);
        bool dds = TextureFormats.IsDds(format);
        int padding = dds ? 4 : 2, align = dds ? 4 : 1;

        var mode = regions.All(r => Writable(r.File)) ? AtlasMode.Repack : AtlasMode.Append;

        // Images to place: in repack mode the others' images too (cropped from the old atlas); identical pixels share a region.
        var images = new Dictionary<string, PixelImage>();          // hash → image
        var keyOf = new Dictionary<(string Brush, string State), string>();
        string Add(PixelImage image)
        {
            string hash = ImageImport.Hash(image);
            images.TryAdd(hash, image);
            return hash;
        }
        foreach (var (state, image) in request.Images) keyOf[(request.BrushName, state)] = Add(image);
        if (mode == AtlasMode.Repack)
        {
            foreach (var r in regions) keyOf[(r.Brush, r.State)] = Add(oldAtlas!.Crop(r.Box));
        }

        var newHashes = mode == AtlasMode.Repack ? images.Keys.ToList() : request.Images.Values.Select(ImageImport.Hash).Distinct().ToList();
        var fixedRegions = mode == AtlasMode.Append ? regions.Select(r => r.Box).Distinct().ToList() : [];
        var pack = AtlasPacker.Pack(newHashes.Select(h => new PackItem(h, images[h].Width, images[h].Height)).ToList(), padding, align, fixedRegions,
            minWidth: mode == AtlasMode.Append ? oldAtlas?.Width ?? 1 : 1,
            minHeight: mode == AtlasMode.Append ? oldAtlas?.Height ?? 1 : 1)
            ?? throw new InvalidOperationException("The images don't fit into a 4096×4096 texture. Use a new texture.");

        var atlas = AtlasPacker.Compose(pack.Width, pack.Height, mode == AtlasMode.Append ? oldAtlas : null,
            newHashes.Select(h => (images[h], pack.Boxes[h])), padding);

        var changes = new ChangeSet(request.IsNew ? $"New brush {request.BrushName}" : $"Edit brush {request.BrushName}");
        string extension = existingTexture != null ? Path.GetExtension(existingTexture.RelativePath) : TextureFormats.Extension(format);
        string texturePath = existingTexture?.RelativePath ?? $"Textures/{texture}{extension}";
        changes.Set(new FileChange(texturePath, null,
            $"{(existingTexture is null ? "New" : mode == AtlasMode.Repack ? "Repacked" : "Added to")} {pack.Width}×{pack.Height} {format}",
            TextureFormats.Encode(atlas, format)));

        // .brushes files: new boxes for moved regions, then the edited brush itself.
        var docs = new Dictionary<string, SinsDocument>(StringComparer.OrdinalIgnoreCase);
        async Task<SinsDocument> Doc(string file)
        {
            if (docs.TryGetValue(file, out var d)) return d;
            var resolved = workspace.Stack.Resolve(file);
            d = resolved is null
                ? SinsDocument.Parse("TXT\r\nonlyLoadOnDemand FALSE\r\nbrushCount 0\r\n"u8.ToArray())
                : SinsDocument.Load(await workspace.Cache.GetTextPathAsync(resolved.AbsolutePath, ct));
            return docs[file] = d;
        }

        int moved = 0;
        if (mode == AtlasMode.Repack)
        {
            foreach (var r in regions)
            {
                var box = pack.Boxes[keyOf[(r.Brush, r.State)]];
                if (box == r.Box) continue;
                SetImage(await Doc(r.File), r.Brush, r.State, texture, box);
                moved++;
            }
        }

        var target = await Doc(brushesFile);
        var newImages = request.Images.ToDictionary(kv => kv.Key, kv => pack.Boxes[keyOf[(request.BrushName, kv.Key)]]);
        WriteBrush(target, request.BrushName, texture, newImages);
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

        if (mode == AtlasMode.Append && existingTexture != null && !mod.Contains(existingTexture.RelativePath))
            changes.Notes.Add($"'{texture}' comes from {existingTexture.Layer.Name}. The mod gets its own copy with the new images added; " +
                              "every existing image stays where it was, so brushes elsewhere keep working.");
        if (pack.Width > 2048 || pack.Height > 2048)
            changes.Notes.Add($"The atlas is {pack.Width}×{pack.Height}. Very large textures use a lot of video memory.");

        return new BrushEditPlan(changes, mode, pack.Width, pack.Height, moved, atlas);
    }

    private static bool SameTexture(string fileName, string texture) =>
        string.Equals(Path.GetFileNameWithoutExtension(fileName), texture, StringComparison.OrdinalIgnoreCase);

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
    private static void WriteBrush(SinsDocument doc, string brush, string texture, IReadOnlyDictionary<string, PixelBox> images)
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
            lines.Add(SinsLine.Create("\t", "fileName", SinsValue.FormatString(texture), e));
            lines.Add(SinsLine.Create("\t", "pixelBox", Box(images[Brush.SimpleState]), e));
        }
        else
        {
            foreach (string state in BrushStates.All.Where(images.ContainsKey).Concat(images.Keys.Where(k => !BrushStates.All.Contains(k))))
            {
                lines.Add(SinsLine.Create("\t", state, "", e));
                lines.Add(SinsLine.Create("\t\t", "fileName", SinsValue.FormatString(texture), e));
                lines.Add(SinsLine.Create("\t\t", "pixelBox", Box(images[state]), e));
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
