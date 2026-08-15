namespace BIM.DatViet.Domain;

/// <summary>
/// Mass of reinforcement, as a pure kernel: no Revit API, no I/O, millimeters and kilograms only.
///
/// The unit mass is computed from the declared steel density and the nominal circle area of the
/// bar, never looked up in a table of diameters. A table only answers for the diameters somebody
/// typed into it; the day a rule states D18 or D22 a lookup either returns nothing or, worse,
/// returns the nearest row. The computed value reproduces the TCVN 1651 nominal masses to the three
/// decimals a bar bending schedule prints, so nothing is lost by not tabulating them.
///
/// The area comes from <see cref="AbutmentRebarMaterial.EffectiveAreaMm2"/>, the same property
/// §5.11.2.1 develops a bar on, so a schedule and an anchorage length can never disagree about how
/// big a bar is.
/// </summary>
public static class AbutmentBarMassKernel
{
    /// <summary>
    /// Density band a declared value has to fall inside. Structural carbon steel sits at 7850
    /// kg/m³; the band is wide enough for any grade a supplier states and narrow enough that a
    /// value in g/cm³ or kg/dm³ cannot pass as one in kg/m³.
    /// </summary>
    public const double MinimumSteelDensityKgPerCubicMetre = 7000;

    /// <inheritdoc cref="MinimumSteelDensityKgPerCubicMetre"/>
    public const double MaximumSteelDensityKgPerCubicMetre = 8500;

    /// <summary>
    /// Mass per running metre in kg/m: density × nominal circle area. The 1e-6 converts mm² to m²,
    /// which leaves kg/m³ × m² = kg/m.
    /// </summary>
    public static bool TryUnitMassKgPerMetre(
        double barDiameterMm,
        double steelDensityKgPerCubicMetre,
        out double unitMassKgPerMetre,
        out string error)
    {
        unitMassKgPerMetre = 0;
        error = string.Empty;
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_MASS_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(steelDensityKgPerCubicMetre) ||
            steelDensityKgPerCubicMetre < MinimumSteelDensityKgPerCubicMetre ||
            steelDensityKgPerCubicMetre > MaximumSteelDensityKgPerCubicMetre)
        {
            error = $"ABUTMENT_MASS_STEEL_DENSITY_INVALID: khối lượng riêng thép " +
                    $"{steelDensityKgPerCubicMetre} kg/m³ phải nằm trong dải " +
                    $"[{MinimumSteelDensityKgPerCubicMetre:0.#}; {MaximumSteelDensityKgPerCubicMetre:0.#}].";
            return false;
        }

        var areaMm2 = new AbutmentRebarMaterial { BarDiameterMm = barDiameterMm }.EffectiveAreaMm2;
        unitMassKgPerMetre = steelDensityKgPerCubicMetre * areaMm2 * 1e-6;
        return true;
    }

    /// <summary>Mass of one cut length in kilograms.</summary>
    public static bool TryMassKg(
        double unitMassKgPerMetre,
        double lengthMm,
        out double massKg,
        out string error)
    {
        massKg = 0;
        error = string.Empty;
        if (!double.IsFinite(unitMassKgPerMetre) || unitMassKgPerMetre <= 0)
        {
            error = $"ABUTMENT_MASS_UNIT_MASS_INVALID: khối lượng đơn vị {unitMassKgPerMetre} kg/m " +
                    "không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(lengthMm) || lengthMm <= 0)
        {
            error = $"ABUTMENT_MASS_LENGTH_INVALID: chiều dài {lengthMm} mm không dương hữu hạn.";
            return false;
        }

        massKg = unitMassKgPerMetre * lengthMm / 1000;
        return true;
    }
}
