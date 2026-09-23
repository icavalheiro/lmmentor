using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.UnitTests;

public class ModelAvailabilityTests
{
    // 2026-09-23 é uma quarta-feira; os horários abaixo são hora local do servidor.
    private static DateTime Wednesday(int hour, int minute = 0) => new(2026, 9, 23, hour, minute, 0, DateTimeKind.Local);

    [Fact]
    public void IsBlockedAt_ReturnsFalse_WhenThereAreNoWindows()
    {
        Assert.False(ModelAvailability.IsBlockedAt(null, Wednesday(10)));
        Assert.False(ModelAvailability.IsBlockedAt([], Wednesday(10)));
    }

    [Fact]
    public void IsBlockedAt_ReturnsTrue_InsideASameDayWindow()
    {
        var windows = new List<ModelAvailabilityWindow>
        {
            new() { DaysOfWeek = [DayOfWeek.Wednesday], StartTime = "09:00", EndTime = "18:00" },
        };

        Assert.True(ModelAvailability.IsBlockedAt(windows, Wednesday(9)));
        Assert.True(ModelAvailability.IsBlockedAt(windows, Wednesday(17, 59)));
    }

    [Fact]
    public void IsBlockedAt_ReturnsFalse_OutsideASameDayWindow()
    {
        var windows = new List<ModelAvailabilityWindow>
        {
            new() { DaysOfWeek = [DayOfWeek.Wednesday], StartTime = "09:00", EndTime = "18:00" },
        };

        Assert.False(ModelAvailability.IsBlockedAt(windows, Wednesday(8, 59)));
        // O fim da janela é exclusivo.
        Assert.False(ModelAvailability.IsBlockedAt(windows, Wednesday(18)));
    }

    [Fact]
    public void IsBlockedAt_ReturnsFalse_OnADayNotCoveredByTheWindow()
    {
        var windows = new List<ModelAvailabilityWindow>
        {
            new() { DaysOfWeek = [DayOfWeek.Monday], StartTime = "09:00", EndTime = "18:00" },
        };

        Assert.False(ModelAvailability.IsBlockedAt(windows, Wednesday(10)));
    }

    [Fact]
    public void IsBlockedAt_HandlesWindowsCrossingMidnight()
    {
        var windows = new List<ModelAvailabilityWindow>
        {
            new() { DaysOfWeek = [DayOfWeek.Tuesday], StartTime = "22:00", EndTime = "06:00" },
        };

        // Terça 23:00 está dentro da janela que começou no mesmo dia.
        Assert.True(ModelAvailability.IsBlockedAt(windows, new DateTime(2026, 9, 22, 23, 0, 0, DateTimeKind.Local)));
        // Quarta 05:00 ainda é a continuação da janela iniciada na terça.
        Assert.True(ModelAvailability.IsBlockedAt(windows, Wednesday(5)));
        Assert.False(ModelAvailability.IsBlockedAt(windows, Wednesday(7)));
    }

    [Fact]
    public void IsBlockedAt_IgnoresWindowsWithInvalidTimes()
    {
        var windows = new List<ModelAvailabilityWindow>
        {
            new() { DaysOfWeek = [DayOfWeek.Wednesday], StartTime = "not-a-time", EndTime = "18:00" },
        };

        Assert.False(ModelAvailability.IsBlockedAt(windows, Wednesday(10)));
    }
}
