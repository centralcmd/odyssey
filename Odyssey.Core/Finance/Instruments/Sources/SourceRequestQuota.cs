using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sources;

/// <summary>Reserves one wire request against a source's daily cap (issue #446 §4.4).</summary>
public interface ISourceRequestQuota
{
    Task<bool> TryReserveAsync(string sourceKey, int maxPerDay, CancellationToken cancellationToken);
}

/// <summary>
/// The daily counter on <see cref="InstrumentPriceSourceState"/>: ensure the row, roll over to today's Oslo date,
/// reserve if under the cap, save with the <c>Version</c> token; a conflict re-reads and retries up to five
/// times, then fails closed (an unsent request is the safe outcome). A tracked update, never
/// <c>ExecuteUpdateAsync</c>, so the same code runs on EF InMemory and MariaDB. Each reservation uses its own
/// scope, so a reservation commits whatever the caller's own context does.
/// </summary>
public sealed class SourceRequestQuota(IServiceScopeFactory scopes, TimeProvider timeProvider) : ISourceRequestQuota
{
    public const int MaxConflicts = 5;

    public async Task<bool> TryReserveAsync(string sourceKey, int maxPerDay, CancellationToken cancellationToken)
    {
        var today = OsloDate.Today(timeProvider);
        for (var attempt = 0; attempt < MaxConflicts; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var state = await context.InstrumentPriceSourceStates.FirstOrDefaultAsync(s => s.SourceKey == sourceKey, cancellationToken);
            if (state is null)
            {
                state = new InstrumentPriceSourceState { SourceKey = sourceKey, RequestDay = today, RequestCount = 0, UpdatedAtUtc = Now };
                context.InstrumentPriceSourceStates.Add(state);
            }

            if (state.RequestDay != today)
            {
                state.RequestDay = today;
                state.RequestCount = 0;
            }

            if (state.RequestCount >= maxPerDay)
            {
                return false;
            }

            state.RequestCount++;
            state.UpdatedAtUtc = Now;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another replica reserved first: re-read and try again.
            }
            catch (DbUpdateException exception) when (InstrumentDbErrors.IsDuplicateKey(exception))
            {
                // Another replica created the row first: re-read and try again.
            }
        }

        return false;
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;
}
