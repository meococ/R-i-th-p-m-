using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using BIM.DatViet.Domain;
using BIM.DatViet.Models;

namespace BIM.DatViet.Infrastructure;

public static class AbutmentPresetStore
{
    public const string DefaultPresetFileName = "cau-van-cui-m2.v1.json";

    /// <summary>Embedded in the DLL. The baseline every other source is compared against.</summary>
    public const int EmbeddedCatalogPriority = 0;
    /// <summary>Shipped next to the DLL; byte-checked against the embedded copy on load.</summary>
    public const int PackagedCatalogPriority = 10;
    /// <summary>
    /// Dropped into the user folder. Outranks everything and passes no integrity check, so loading
    /// one is always reported.
    /// </summary>
    public const int UserCatalogPriority = 100;

    public static string UserPresetDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DVB_ADDIN", "Profiles", "Abutment");

    public static AbutmentRebarPresetV1 LoadDefault() => LoadNamed(DefaultPresetFileName);

    public static IReadOnlyList<AbutmentRebarPresetV1> LoadAll()
    {
        var assembly = typeof(AbutmentPresetStore).Assembly;
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        var presetDirectory = Path.Combine(assemblyDirectory, "Resources", "Presets");
        var sources = new SortedDictionary<string, (
            string Json,
            string BaseDirectory,
            int Priority,
            DateTime TimestampUtc)>(StringComparer.OrdinalIgnoreCase);

        const string resourcePrefix = "BIM.DatViet.Resources.Presets.";
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal) &&
                                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidDataException($"Không đọc được embedded preset '{resourceName}'.");
            using var reader = new StreamReader(stream);
            sources[resourceName[resourcePrefix.Length..]] =
                (reader.ReadToEnd(), presetDirectory, EmbeddedCatalogPriority, DateTime.MinValue);
        }

        if (Directory.Exists(presetDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(presetDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                var packagedJson = File.ReadAllText(path);
                if (sources.TryGetValue(fileName, out var embedded))
                    AbutmentPresetPackageIntegrity.EnsureMatch(fileName, embedded.Json, packagedJson);
                sources[fileName] =
                    (packagedJson, presetDirectory, PackagedCatalogPriority, File.GetLastWriteTimeUtc(path));
            }
        }

        if (Directory.Exists(UserPresetDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(UserPresetDirectory, "*.json", SearchOption.TopDirectoryOnly))
                sources["user:" + Path.GetFileName(path)] =
                    (File.ReadAllText(path), UserPresetDirectory, UserCatalogPriority, File.GetLastWriteTimeUtc(path));
        }

        if (sources.Count == 0)
            throw new FileNotFoundException("Không tìm thấy profile mố cầu.", presetDirectory);

        var profiles = sources.Select(source => Parse(
            source.Value.Json,
            source.Value.BaseDirectory,
            source.Value.Priority,
            source.Value.TimestampUtc)).ToList();
        foreach (var duplicate in profiles.GroupBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            throw new InvalidDataException($"ProfileId '{duplicate.Key}' bị trùng trong catalog mố cầu.");
        return profiles;
    }

    public static string SaveUserProfile(AbutmentRebarPresetV1 preset)
    {
        if (string.IsNullOrWhiteSpace(preset.ProfileId))
            throw new InvalidDataException("Thiếu profileId cho profile người dùng.");
        var existing = LoadAll();
        if (existing.Any(item => item.ProfileId.Equals(
                preset.ProfileId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"ProfileId '{preset.ProfileId}' đã tồn tại; không ghi đè.");

        var clone = preset.Clone();
        clone.RuleHash = clone.ComputeHash();
        var blocking = clone.Validate().Where(issue => issue.IsError).ToList();
        if (blocking.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine,
                blocking.Select(issue => $"[{issue.Code}] {issue.Message}")));

        Directory.CreateDirectory(UserPresetDirectory);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var fileName = new string(clone.ProfileId
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray()) + ".json";
        var path = Path.Combine(UserPresetDirectory, fileName);
        if (File.Exists(path))
            throw new IOException($"Profile đích '{path}' đã tồn tại; không ghi đè.");
        File.WriteAllText(path, JsonSerializer.Serialize(clone, AbutmentRebarPresetV1.JsonOptions));
        return path;
    }

    private static AbutmentRebarPresetV1 LoadNamed(string fileName)
    {
        var assembly = typeof(AbutmentPresetStore).Assembly;
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        var presetDirectory = Path.Combine(assemblyDirectory, "Resources", "Presets");
        var path = Path.Combine(presetDirectory, fileName);
        using var stream = assembly.GetManifestResourceStream("BIM.DatViet.Resources.Presets." + fileName)
                           ?? throw new FileNotFoundException("Embedded abutment preset is missing.", path);
        using var reader = new StreamReader(stream);
        var embeddedJson = reader.ReadToEnd();
        if (File.Exists(path))
        {
            var packagedJson = File.ReadAllText(path);
            AbutmentPresetPackageIntegrity.EnsureMatch(fileName, embeddedJson, packagedJson);
            return Parse(packagedJson, presetDirectory, 10, File.GetLastWriteTimeUtc(path));
        }

        return Parse(embeddedJson, presetDirectory, 0, DateTime.MinValue);
    }

    private static AbutmentRebarPresetV1 Parse(
        string json,
        string evidenceBaseDirectory,
        int priority,
        DateTime timestampUtc)
    {
        var preset = JsonSerializer.Deserialize<AbutmentRebarPresetV1>(
                         json, AbutmentRebarPresetV1.JsonOptions)
                     ?? throw new InvalidDataException("Abutment preset JSON is empty or invalid.");
        var declaredRuleHash = preset.RuleHash;
        var computedRuleHash = preset.ComputeHash();

        var loadIssues = ValidateEvidenceFiles(preset, evidenceBaseDirectory);
        if (!string.IsNullOrWhiteSpace(declaredRuleHash) &&
            !declaredRuleHash.Equals(computedRuleHash, StringComparison.OrdinalIgnoreCase))
        {
            loadIssues.Insert(0, new AbutmentPresetIssue
            {
                Code = "ABUTMENT_RULE_HASH_MISMATCH",
                Message = "ruleHash khai báo không khớp nội dung preset.",
                IsError = true
            });
        }
        // A user-folder profile outranks both the embedded and the packaged copy and is the one
        // source no integrity check covers. Silence there would let an unreviewed file decide what
        // steel gets placed, so say it out loud every load.
        if (priority >= UserCatalogPriority)
        {
            loadIssues.Add(new AbutmentPresetIssue
            {
                Code = "ABUTMENT_PRESET_USER_SOURCE_UNVERIFIED",
                Message =
                    $"Profile '{preset.ProfileId}' nạp từ thư mục người dùng ({UserPresetDirectory}), " +
                    $"không qua kiểm toàn vẹn nào và có độ ưu tiên {priority} — cao hơn cả bản đóng " +
                    $"gói theo DLL. Xác nhận đúng nguồn trước khi tạo thép.",
                IsError = false
            });
        }

        preset.RuleHash = computedRuleHash;
        preset.EvidenceLoadIssues = loadIssues;
        preset.CatalogPriority = priority;
        preset.CatalogTimestampUtc = timestampUtc;
        return preset;
    }

    /// <summary>
    /// Issues from evidence file checks (missing/hash). Empty when all good.
    /// <para>
    /// Authorized evidence that is missing or altered is a hard error, but it is reported rather
    /// than thrown. Throwing here escaped all the way out of <see cref="LoadAll"/> into the
    /// controller constructor, so one absent PDF stopped the command from opening at all — for
    /// every profile in the catalog, not just the affected one. The gate itself is unchanged:
    /// <c>IsError</c> issues reach <c>plan.Issues</c> as <c>ValidationSeverity.Error</c>, which
    /// still blocks Create. Only the tier moved: the tool opens and says why, instead of dying
    /// before the engineer can read anything.
    /// </para>
    /// </summary>
    private static List<AbutmentPresetIssue> ValidateEvidenceFiles(
        AbutmentRebarPresetV1 preset, string evidenceBaseDirectory)
    {
        var issues = new List<AbutmentPresetIssue>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evidence in preset.SourceEvidence)
        {
            var evidencePath = Path.IsPathRooted(evidence.FilePath)
                ? evidence.FilePath
                : Path.GetFullPath(Path.Combine(evidenceBaseDirectory, evidence.FilePath));
            if (!File.Exists(evidencePath))
            {
                var blocking = AbutmentEvidencePolicy.ShouldBlockLoad(
                    preset.Topology.RequireEvidenceFiles, evidence);
                issues.Add(new AbutmentPresetIssue
                {
                    Code = "ABUTMENT_EVIDENCE_FILE_MISSING",
                    Message = blocking
                        ? $"Hồ sơ '{evidence.EvidenceId}' được ủy quyền để tạo thép nhưng không đọc " +
                          $"được tại '{evidencePath}'; Create bị khóa cho tới khi có đúng file này. " +
                          $"Đặt lại file vào đường dẫn trên, hoặc sửa filePath trong profile " +
                          $"'{preset.ProfileId}' thành đường dẫn tương đối theo thư mục preset."
                        : $"Hồ sơ tham chiếu '{evidence.EvidenceId}' không đọc được tại " +
                          $"'{evidencePath}'; không chặn Create vì nó không được ủy quyền tạo thép.",
                    IsError = blocking
                });
                continue;
            }
            if (!hashes.TryGetValue(evidencePath, out var actual))
            {
                using var stream = new FileStream(evidencePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha = SHA256.Create();
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                hashes[evidencePath] = actual;
            }
            if (!string.IsNullOrWhiteSpace(evidence.Sha256) &&
                !actual.Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var blocking = AbutmentEvidencePolicy.ShouldBlockLoad(
                    preset.Topology.RequireEvidenceFiles, evidence);
                issues.Add(new AbutmentPresetIssue
                {
                    Code = "ABUTMENT_EVIDENCE_HASH_MISMATCH",
                    Message = blocking
                        ? $"Hồ sơ '{evidence.EvidenceId}' tại '{evidencePath}' đã đổi nội dung so với " +
                          $"lúc duyệt (SHA256 khai {evidence.Sha256}, đo được {actual}); Create bị " +
                          $"khóa vì số liệu thép trong profile có thể không còn khớp bản vẽ. Đối " +
                          $"chiếu lại bản vẽ rồi cập nhật sha256 trong profile '{preset.ProfileId}'."
                        : $"Hồ sơ tham chiếu '{evidence.EvidenceId}' đã đổi nội dung so với lúc duyệt " +
                          $"(SHA256 khai {evidence.Sha256}, đo được {actual}); không chặn Create vì " +
                          $"nó không được ủy quyền tạo thép.",
                    IsError = blocking
                });
            }
        }
        return issues;
    }
}
