using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sources;

/// <summary>The outcome of one reservation: reserved, or which cap stopped it.</summary>
public enum QuotaReservation
{
    Reserved,

    /// <summary>The per-Oslo-day cap (<see cref="InstrumentPriceSourceLimits.MaxRequestsPerDay"/>) is reached.</summary>
    DayCapReached,

    /// <summary>The per-UTC-hour cap (<see cref="InstrumentPriceSourceLimits.MaxRequestsPerHour"/>) is reached.</summary>
    HourCapReached,

    /// <summary>Every attempt lost a race on the state row's token; nothing was reserved, so nothing may be sent.</summary>
    Contended,
}

/// <summary>Reserves one wire request against a source's daily and hourly caps (issue #446 §4.4, issue #447 A1).</summary>
public interface ISourceRequestQuota
{
    Task<QuotaReservation> TryReserveAsync(string sourceKey, InstrumentPriceSourceLimits limits, CancellationToken cancellationToken);
}

/// <summary>
/// The request counters on <see cref="InstrumentPriceSourceState"/>: ensure the row, roll the day over to today's Oslo
/// date and the hour forward to the current UTC hour, reserve if under <strong>both</strong> caps, save once with the
/// <c>Version</c> token; a conflict re-reads and retries up to five times, then fails closed (an unsent request is
/// the safe outcome). The hour bucket <strong>only moves forward</strong>: a replica whose clock lags the one that
/// last wrote counts against the newer bucket instead of resetting it, so a skewed clock cannot buy a fresh hourly
/// allowance. A tracked update, never <c>ExecuteUpdateAsync</c>, so the same code runs on EF InMemory and MariaDB.
/// Each reservation uses its own scope, so a reservation commits whatever the caller's own context does.
/// </summary>
public sealed class SourceRequestQuota(IServiceScopeFactory scopes, TimeProvider timeProvider) : ISourceRequestQuota
{
    public const int MaxConflicts = 5;

    public async Task<QuotaReservation> TryReserveAsync(
        string sourceKey, InstrumentPriceSourceLimits limits, CancellationToken cancellationToken)
    {
        var today = OsloDate.Today(timeProvider);
        var hour = HourOf(timeProvider.GetUtcNow().UtcDateTime);
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

            if (state.RequestHourUtc is not { } stored || hour > stored)
            {
                state.RequestHourUtc = hour;
                state.RequestHourCount = 0;
            }

            if (state.RequestCount >= limits.MaxRequestsPerDay)
            {
                return QuotaReservation.DayCapReached;
            }

            if (limits.MaxRequestsPerHour is { } perHour && state.RequestHourCount >= perHour)
            {
                return QuotaReservation.HourCapReached;
            }

            state.RequestCount++;
            state.RequestHourCount++;
            state.UpdatedAtUtc = Now;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return QuotaReservation.Reserved;
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

        return QuotaReservation.Contended;
    }

    /// <summary>The UTC clock hour <paramref name="utc"/> falls in.</summary>
    public static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;
}
