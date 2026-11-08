using Xunit;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Tests;

/// <summary>The today + 730 edge of a window write reads the host's clock (issue #436 §3.1 item 3).</summary>
public sealed class WeatherPointValidationClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static List<ValidationResult> Validate(DateOnly to)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(new FakeTimeProvider(Now)).BuildServiceProvider();
        var update = new UpdateWeatherPoint
        {
            HistoryEnabled = true,
            ForecastEnabled = false,
            IsTracked = false,
            SettingsVersion = Guid.NewGuid(),
            HistoryWindows = [new WeatherHistoryWindowDto { From = new DateOnly(2026, 1, 1), To = to }],
        };
        return [.. update.Validate(new ValidationContext(update, services, null))];
    }

    [Fact]
    public void The_last_allowed_day_is_today_plus_730_on_the_injected_clock()
    {
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.Empty(Validate(today.AddDays(WeatherRules.HistoryWindowMaxDaysAhead)));
        var error = Assert.Single(Validate(today.AddDays(WeatherRules.HistoryWindowMaxDaysAhead + 1)));
        Assert.Equal(["HistoryWindows[0].To"], error.MemberNames);
    }
}
