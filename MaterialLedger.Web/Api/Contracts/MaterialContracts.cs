namespace MaterialLedger.Web.Api.Contracts;

public sealed record MaterialSpecContract(
    long Id,
    string MaterialFamily,
    string? Grade,
    string? Finish,
    decimal ThicknessIn,
    string DisplayName,
    string? GaugeLabel);
