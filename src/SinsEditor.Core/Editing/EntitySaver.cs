using SinsEditor.Core.Conversion;
using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core.Editing;

public enum SaveFormat
{
    Text,
    /// <summary>Written as text, then converted with ConvertData.</summary>
    Binary,
}

/// <summary>
/// Writes an edited entity into the edited mod. An entity from a lower layer becomes an override at the same path.
/// Text follows the base game's conventions (<see cref="ConventionCheck.ExpectedConvention"/>). Files are written to a
/// temporary file first and then moved into place, so a failed save never leaves a half-written file.
/// </summary>
public sealed class EntitySaver(ModWorkspace workspace)
{
    public async Task<EntityRecord> SaveAsync(EntityEditSession session, SaveFormat format, CancellationToken ct = default)
    {
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        string relative = session.Original.File.RelativePath;
        string target = Path.Combine(mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var doc = SinsDocument.Parse(session.Document.ToBytes());
        doc.ApplyConvention(ConventionCheck.ExpectedConvention(workspace.Stack, relative, doc.HasNonAsciiText));

        string temp = target + SafeWrite.TempSuffix;
        try
        {
            if (format == SaveFormat.Binary)
            {
                string text = Path.Combine(Path.GetTempPath(), "SinsEditor", Guid.NewGuid().ToString("N") + Path.GetExtension(target));
                Directory.CreateDirectory(Path.GetDirectoryName(text)!);
                try
                {
                    doc.Save(text);
                    await ConvertDataTool.FromGameDirectory(workspace.Options.GameDirectory).ConvertAsync(text, temp, ConvertTarget.Binary, ct);
                }
                finally
                {
                    File.Delete(text);
                }
            }
            else
            {
                doc.Save(temp);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        mod.Refresh();
        var resolved = workspace.Stack.Resolve(relative) ?? throw new IOException($"'{relative}' wasn't found after saving.");
        string type = doc.Lines.FirstOrDefault(l => l.Key == "entityType") is { } t && SinsValue.TryGetString(t.Value, out var s) ? s : "";
        return session.Original with { EntityType = type, File = resolved, Document = doc };
    }
}
