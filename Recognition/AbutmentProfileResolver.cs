using Autodesk.Revit.DB;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

public sealed class AbutmentProfileResolver
{
    private readonly Document _document;

    public AbutmentProfileResolver(Document document) => _document = document;

    public AbutmentRebarPresetV1 Resolve(Element host)
    {
        var identityMatches = AbutmentPresetStore.LoadAll()
            .Where(profile => AbutmentGeometryExtractor.MatchesProfileIdentity(host, profile))
            .ToList();
        if (identityMatches.Count == 0)
            throw new InvalidOperationException(
                $"Không có profile mố cầu nào khớp host {ElementIdCompat.ToLong(host.Id)} ({host.Name}).");

        var validation = identityMatches.Select(profile => new
        {
            Profile = profile,
            Errors = profile.Validate().Where(issue => issue.IsError).ToList()
        }).ToList();
        var validIdentityMatches = validation
            .Where(item => item.Errors.Count == 0)
            .Select(item => item.Profile)
            .ToList();
        if (validIdentityMatches.Count == 0)
        {
            var details = string.Join(" | ", validation.Select(item =>
                $"{item.Profile.ProfileId}: " +
                string.Join("; ", item.Errors.Take(6).Select(issue =>
                    $"[{issue.Code}] {issue.Message}"))));
            throw new InvalidOperationException(
                $"Profile khớp host nhưng không vượt qua design gate. {details}");
        }
        if (validIdentityMatches.Count == 1)
            return validIdentityMatches[0];

        var geometryMatches = validIdentityMatches.Where(profile =>
        {
            var extraction = new AbutmentGeometryExtractor(_document).Extract(host, profile);
            return extraction.Status == AbutmentResolutionStatus.Resolved &&
                   extraction.Issues.All(issue => issue.Severity != ValidationSeverity.Error);
        }).ToList();
        if (geometryMatches.Count == 1)
            return geometryMatches[0];
        if (geometryMatches.Count > 1)
        {
            var highestPriority = geometryMatches.Max(profile => profile.CatalogPriority);
            var prioritized = geometryMatches.Where(profile =>
                profile.CatalogPriority == highestPriority).ToList();
            var latest = prioritized.Max(profile => profile.CatalogTimestampUtc);
            var latestProfiles = prioritized.Where(profile =>
                profile.CatalogTimestampUtc == latest).ToList();
            if (latestProfiles.Count == 1) return latestProfiles[0];
        }

        var candidateIds = string.Join(", ", validIdentityMatches.Select(profile => profile.ProfileId));
        throw new InvalidOperationException(
            $"Host {ElementIdCompat.ToLong(host.Id)} khớp nhiều profile hoặc không profile nào vượt qua geometry gate: {candidateIds}.");
    }
}
