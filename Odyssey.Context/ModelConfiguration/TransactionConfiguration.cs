using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> entity)
    {
        entity.HasMany(transaction => transaction.TransactionTags)
            .WithMany(tag => tag.Transactions)
            .UsingEntity<TransactionTagLink>(
                right => right
                    .HasOne(link => link.TransactionTag)
                    .WithMany(tag => tag.TransactionTagLinks)
                    .HasForeignKey(link => link.TransactionTagId)
                    // Restrict so an in-use tag cannot be hard-deleted (matches the prior single-tag behavior).
                    .OnDelete(DeleteBehavior.Restrict),
                left => left
                    .HasOne(link => link.Transaction)
                    .WithMany(transaction => transaction.TransactionTagLinks)
                    .HasForeignKey(link => link.TransactionId)
                    // Cascade so a transaction's tag links die with it.
                    .OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.HasKey(link => link.Id);
                    join.HasIndex(link => new { link.TransactionId, link.TransactionTagId }).IsUnique();
                });

        // Non-unique performance indexes backing the server-side list sort/filter (issue #277).
        // Only the missing ones are added; Files/TaxStatements/ExchangeRates are already indexed.
        entity.HasIndex(transaction => transaction.TimeStamp);
    }
}
