using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BIM.DatViet.Models;

public sealed class ManholeRebarPreset
{
    public string Name { get; set; } = "Hố ga - cần kỹ sư xác nhận";
    public string SlabBarTypeName { get; set; } = "D10";
    public string WallVerticalBarTypeName { get; set; } = "D10";
    public string WallHorizontalBarTypeName { get; set; } = "D10";
    public string OpeningBarTypeName { get; set; } = "D10";
    public double CoverMm { get; set; } = 35;
    public double SlabSpacingMm { get; set; } = 150;
    public double WallVerticalSpacingMm { get; set; } = 150;
    public double WallHorizontalSpacingMm { get; set; } = 150;
    public double LayerGapMm { get; set; }
    public double EndClearanceMm { get; set; } = 35;
    public double OpeningClearanceMm { get; set; } = 50;
    public double MinRetainedLengthMm { get; set; } = 300;
    public double CornerLegMm { get; set; } = 500;
    public int CornerBarCount { get; set; } = 2;
    public int OpeningEdgeBarCount { get; set; } = 2;
    public bool AddDiagonalOpeningBars { get; set; }
    public bool EngineerConfirmed { get; set; }

    public string ComputeHash()
    {
        var json = JsonSerializer.Serialize(this);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    public ManholeRebarPreset Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<ManholeRebarPreset>(json) ?? new ManholeRebarPreset();
    }
}
