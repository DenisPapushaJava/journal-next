using System.Globalization;
using Jornal.Core.Pricing;

namespace Jornal.Desktop.ViewModels;

internal static class PricePreviewInput
{
    internal static bool TryBuild(string cuttingType, string thicknessText, string lengthText, string coefficientText,
        string materialId, bool requiresHansId, out CuttingPriceRequest request)
    {
        request = null!;
        bool Number(string text, out decimal value) => decimal.TryParse(text.Trim().Replace(',', '.'),
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        if (cuttingType.Length == 0 || !Number(thicknessText, out var thickness) || thickness <= 0 ||
            !Number(coefficientText, out var coefficient) || coefficient <= 0 ||
            !int.TryParse(lengthText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 ||
            requiresHansId && materialId.Trim() is not ("-1" or "0" or "1" or "2")) return false;
        request = new(cuttingType, thickness, length, coefficient, cuttingType == "HansLaser" ? materialId.Trim() : "");
        return true;
    }
}
