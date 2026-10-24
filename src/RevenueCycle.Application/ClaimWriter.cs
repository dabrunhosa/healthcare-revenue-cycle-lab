using System.Globalization;
using RevenueCycle.Domain;

namespace RevenueCycle.Application;

public static class ClaimWriter
{
    public static Claim Create(ClinicalContext clinical, BillingContext billing, string claimId, DateTimeOffset now)
    {
        var segments = new List<string>
        {
            "ST*837*0001*005010X222A1",
            $"BHT*0019*00*{claimId}*{now:yyyyMMdd}*{now:HHmm}*CH",
            "NM1*41*2*SYNTHETIC LAB*****46*LAB",
            "PER*IC*LAB*TE*5550100000",
            "NM1*40*2*MOCK PAYER*****46*MOCK",
            "HL*1**20*1",
            "NM1*85*2*SYNTHETIC PROVIDER*****XX*1999999984",
            "HL*2*1*22*0",
            "SBR*P*18*******CI",
            $"NM1*IL*1*SYNTHETIC*PATIENT****MI*{clinical.PatientId}",
            "NM1*PR*2*MOCK PAYER*****PI*MOCK",
            $"CLM*{claimId}*{Money(billing.Total)}***11:B:1*Y*A*Y*I",
            $"HI*ABK:{billing.DiagnosisCode.Replace(".", "") }"
        };
        for (var index = 0; index < billing.Lines.Length; index++)
        {
            var line = billing.Lines[index];
            segments.Add($"LX*{index + 1}");
            segments.Add($"SV1*HC:{line.ProcedureCode}*{Money(line.Units * line.UnitPrice)}*UN*{line.Units}***1");
            segments.Add($"DTP*472*D8*{clinical.ServiceDate:yyyyMMdd}");
        }
        segments.Add($"SE*{segments.Count + 1}*0001");
        var envelope = new[]
        {
            $"ISA*00*          *00*          *ZZ*LAB            *ZZ*MOCK           *{now:yyMMdd}*{now:HHmm}*^*00501*000000001*0*T*:",
            $"GS*HC*LAB*MOCK*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X222A1"
        };
        return new(claimId, billing.Total, "USD", string.Join("~\n", envelope.Concat(segments).Concat(["GE*1*1", "IEA*1*000000001"])) + "~");
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
