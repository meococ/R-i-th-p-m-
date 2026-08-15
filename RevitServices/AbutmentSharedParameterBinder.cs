using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using System.IO;
using System.Reflection;

namespace BIM.DatViet.RevitServices;

public static class AbutmentSharedParameterBinder
{
    public static IReadOnlyList<string> EnsureBound(Document document)
    {
        var issues = new List<string>();
        var application = document.Application;
        var previous = application.SharedParametersFilename;
        var path = ResolveRuntimeFile();
        try
        {
            if (!File.Exists(path)) return new[] { $"Shared parameter file missing: {path}" };
            application.SharedParametersFilename = path;
            var file = application.OpenSharedParameterFile();
            var group = file?.Groups.get_Item("DVB_ABUTMENT");
            if (group is null) return new[] { "Shared parameter group DVB_ABUTMENT is missing." };
            // A previous deployment leaves its own copy of this file beside the DLL, and that copy
            // wins over the embedded one. A stale copy binds fewer parameters than this build
            // writes, which would surface as every newly created bar failing its readback with no
            // hint of why. Name the file and the missing definitions instead.
            var declared = group.Definitions
                .Cast<Definition>()
                .Select(definition => definition.Name)
                .ToHashSet(StringComparer.Ordinal);
            var absent = AbutmentMetadataStore.ExpectedSharedParameterNames
                .Where(name => !declared.Contains(name))
                .ToList();
            if (absent.Count > 0)
                return new[]
                {
                    $"Shared parameter file '{path}' thiếu {absent.Count} tham số " +
                    $"({string.Join(", ", absent)}). Deploy lại " +
                    @"Resources\DVB_Abutment_SharedParameters.txt cùng phiên bản với DLL."
                };

            var categories = application.Create.NewCategorySet();
            categories.Insert(document.Settings.Categories.get_Item(BuiltInCategory.OST_Rebar));
            var binding = application.Create.NewInstanceBinding(categories);
            foreach (Definition definition in group.Definitions)
            {
                if (document.ParameterBindings.Contains(definition)) continue;
#if REVIT2024_OR_GREATER
                if (!document.ParameterBindings.Insert(definition, binding, GroupTypeId.IdentityData))
#else
                if (!document.ParameterBindings.Insert(definition, binding, BuiltInParameterGroup.PG_IDENTITY_DATA))
#endif
                    issues.Add($"Could not bind shared parameter '{definition.Name}'.");
            }
        }
        catch (Exception exception)
        {
            issues.Add(exception.Message);
        }
        finally
        {
            application.SharedParametersFilename = previous;
        }
        return issues;
    }

    private static string ResolveRuntimeFile()
    {
        var assembly = typeof(AbutmentSharedParameterBinder).Assembly;
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        var deployed = Path.Combine(assemblyDirectory, "Resources", "DVB_Abutment_SharedParameters.txt");
        if (File.Exists(deployed)) return deployed;
        using var stream = assembly.GetManifestResourceStream("BIM.DatViet.Resources.DVB_Abutment_SharedParameters.txt")
                           ?? throw new FileNotFoundException("Embedded DVB abutment shared parameter file is missing.");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DVB_ADDIN", "Runtime");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "DVB_Abutment_SharedParameters.txt");
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            File.WriteAllText(path, content);
        return path;
    }
}
