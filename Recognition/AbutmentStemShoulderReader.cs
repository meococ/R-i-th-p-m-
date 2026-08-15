using Autodesk.Revit.DB;
using BIM.DatViet.Domain;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

/// <summary>
/// Measures both footing cantilevers along the local transverse axis from the validated
/// L/Center/R sections. The stem sits off-centre, so these two widths are the physical evidence
/// that separates the toe edge from the heel edge. Recognition reports how far they sit from the
/// profile declaration; planning uses the same measurement to anchor a station chain.
/// </summary>
internal static class AbutmentStemShoulderReader
{
    public static bool TryMeasure(
        AbutmentHostContext context,
        out IReadOnlyList<AbutmentStationDatumKernel.StemShoulder> shoulders,
        out string error)
    {
        shoulders = [];
        error = string.Empty;
        if (context.CoreProfile is not { Sections.Count: > 0 } core)
        {
            error =
                "ABUTMENT_STATION_DATUM_PROFILE_REQUIRED: cần section PROFILE_TM đã kiểm chứng " +
                "để phân biệt cạnh mũi và cạnh gót bệ.";
            return false;
        }

        var measured = new List<AbutmentStationDatumKernel.StemShoulder>(core.Sections.Count);
        foreach (var section in core.Sections)
        {
            if (section.FootingPolygonLocal.Count < 3 || section.StemPolygonLocal.Count < 3)
            {
                error =
                    "ABUTMENT_STATION_DATUM_PROFILE_REQUIRED: section PROFILE_TM thiếu polygon bệ hoặc thân mố.";
                return false;
            }
            measured.Add(new AbutmentStationDatumKernel.StemShoulder(
                UnitUtils.ConvertFromInternalUnits(
                    section.StemPolygonLocal.Min(point => point.Y) -
                    section.FootingPolygonLocal.Min(point => point.Y),
                    UnitTypeId.Millimeters),
                UnitUtils.ConvertFromInternalUnits(
                    section.FootingPolygonLocal.Max(point => point.Y) -
                    section.StemPolygonLocal.Max(point => point.Y),
                    UnitTypeId.Millimeters)));
        }
        shoulders = measured;
        return true;
    }
}
