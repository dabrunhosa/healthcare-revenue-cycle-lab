using System.Text.RegularExpressions;
using RevenueCycle.Domain;

namespace RevenueCycle.Application;

public sealed record PatientInput(string ResourceType, string Id);
public sealed record ReferenceInput(string Reference);
public sealed record CodingInput(string System, string Code);
public sealed record PeriodInput(DateTimeOffset Start, DateTimeOffset End);
public sealed record EncounterInput(string ResourceType, string Id, string Status, ReferenceInput Subject,
    CodingInput Class, PeriodInput Period);
public sealed record IngestRequest(bool Synthetic, PatientInput Patient, EncounterInput Encounter, BillingContext Billing);

public static partial class ClinicalInput
{
    public static ClinicalContext Validate(IngestRequest input)
    {
        if (!input.Synthetic || input.Patient is null || input.Encounter is null || input.Billing is null)
            throw new InputException("Synthetic patient, encounter, and explicit billing context are required.");
        var (patient, encounter, billing) = (input.Patient, input.Encounter, input.Billing);
        if (patient.ResourceType != "Patient" || encounter.ResourceType != "Encounter" ||
            patient.Id is null || encounter.Id is null || !Identifier().IsMatch(patient.Id) || !Identifier().IsMatch(encounter.Id))
            throw new InputException("Resource types and synthetic identifiers are invalid.");
        if (encounter.Status != "finished" || encounter.Class?.Code != "AMB" ||
            encounter.Class.System != "http://terminology.hl7.org/CodeSystem/v3-ActCode" ||
            encounter.Subject?.Reference != $"Patient/{patient.Id}" ||
            encounter.Period is null || encounter.Period.End < encounter.Period.Start ||
            encounter.Period.Start.Year is < 2020 or > 2035 || encounter.Period.End.Year > 2035)
            throw new InputException("A finished ambulatory encounter referencing the supplied patient is required.");
        if (!Diagnosis().IsMatch(billing.DiagnosisCode ?? "") || billing.Lines is not { Length: > 0 and <= 10 } ||
            billing.Lines.Any(line => line is null || !Procedure().IsMatch(line.ProcedureCode ?? "") ||
                line.Units is < 1 or > 100 || line.UnitPrice is <= 0 or > 10000 || decimal.Round(line.UnitPrice, 2) != line.UnitPrice))
            throw new InputException("Billing requires a diagnosis and 1-10 valid synthetic procedure lines.");
        return new(patient.Id, encounter.Id, DateOnly.FromDateTime(encounter.Period.Start.Date));
    }

    [GeneratedRegex("^syn-[A-Za-z0-9-]{1,48}\\z")]
    private static partial Regex Identifier();
    [GeneratedRegex("^[A-Z][0-9]{2}(\\.[A-Z0-9]{1,4})?\\z")]
    private static partial Regex Diagnosis();
    [GeneratedRegex("^DEMO[0-9]{3}\\z")]
    private static partial Regex Procedure();
}

public sealed class InputException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
