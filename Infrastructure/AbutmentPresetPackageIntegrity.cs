using System.IO;

namespace BIM.DatViet.Infrastructure;

/// <summary>
/// Guards against a partial add-in rollout where the packaged JSON beside the DLL
/// differs from its embedded counterpart. Such a mismatch must never select stale rules.
/// </summary>
internal static class AbutmentPresetPackageIntegrity
{
    internal static bool Matches(string embeddedJson, string packagedJson) =>
        string.Equals(embeddedJson, packagedJson, StringComparison.Ordinal);

    internal static void EnsureMatch(string fileName, string embeddedJson, string packagedJson)
    {
        if (Matches(embeddedJson, packagedJson)) return;

        throw new InvalidDataException(
            $"Preset deployed '{fileName}' khác với preset embedded trong DLL. " +
            "Dừng command để tránh dùng rule cũ; deploy lại trọn bộ add-in (DLL và Resources) cùng phiên bản.");
    }
}
