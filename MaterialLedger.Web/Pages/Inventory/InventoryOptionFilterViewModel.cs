namespace MaterialLedger.Web.Pages.Inventory;

public sealed record InventoryOptionFilterChoice(string Value, string Label);

public sealed record InventoryOptionFilterViewModel(
    string DialogId,
    string Label,
    string ActiveFieldName,
    string ValueFieldName,
    bool IsActive,
    IEnumerable<string> SelectedValues,
    IEnumerable<InventoryOptionFilterChoice> Options);
