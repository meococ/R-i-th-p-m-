using BIM.DatViet.Models;

namespace BIM.DatViet.Domain;

public static class AbutmentEvidencePolicy
{
    public static bool ShouldBlockLoad(
        bool requireEvidenceFiles,
        AbutmentSourceEvidence evidence)
    {
        if (!requireEvidenceFiles || !evidence.AuthorizedForCreate)
            return false;

        return !IsReviewOnly(evidence.SuitabilityCode);
    }

    private static bool IsReviewOnly(string suitabilityCode) =>
        suitabilityCode.Equals("CROSS_CHECK_ONLY", StringComparison.OrdinalIgnoreCase) ||
        suitabilityCode.Equals("REFERENCE_ONLY", StringComparison.OrdinalIgnoreCase);
}
