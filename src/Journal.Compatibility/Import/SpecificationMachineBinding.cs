using Jornal.Core.Pricing;

namespace Jornal.Desktop.Import;

internal static class SpecificationMachineBinding
{
    internal static SpecificationImportPreview Bind(SpecificationImportPreview preview, IReadOnlyList<CuttingMachine> machines, string? current)
    {
        var profile = preview.Report.Profile;
        var candidates = machines.Where(machine => machine.SpecificationProfileId == preview.Report.Profile).ToArray();
        var names = candidates.Select(machine => machine.Name).ToList();
        var original = preview.Report.CuttingType?.Value;
        if (original is not null && CuttingMachine.SupportsImport(profile, original, null)) names.Add(original);
        var selected = current is not null && names.Contains(current) ? current : names.Count == 1 ? names[0] : null;
        var fields = preview.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["CuttingType"] = new(selected ?? "", selected is null ? SelectionMessage(profile) : preview.Fields["CuttingType"].Evidence);
        return preview with { Fields = fields };
    }
    internal static string? Validate(SpecificationImportPreview? preview, string name, CuttingMachine? machine) =>
        preview is null || CuttingMachine.SupportsImport(preview.Report.Profile, name, machine) ? null : SelectionMessage(preview.Report.Profile);
    private static string SelectionMessage(string profile) => profile switch
    {
        CuttingMachine.WaterjetProfile => "Выберите станок с импортом AWJC PDF.",
        CuttingMachine.VintechProfile => "Выберите станок с импортом Vintech HTML.",
        CuttingMachine.HansProfile => "Выберите станок с импортом HansLaser PDF.",
        _ => "Выберите станок для формата спецификации."
    };
}
