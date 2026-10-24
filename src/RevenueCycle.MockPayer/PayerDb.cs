using Microsoft.EntityFrameworkCore;

namespace RevenueCycle.MockPayer;

public sealed class ReceiptRow
{
    public string ClaimId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PayerClaimId { get; set; } = "";
}

public sealed class PayerDb(DbContextOptions<PayerDb> options) : DbContext(options)
{
    public DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();
    protected override void OnModelCreating(ModelBuilder builder) => builder.Entity<ReceiptRow>().HasKey(r => r.ClaimId);
}
