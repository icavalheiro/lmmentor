using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Avalia se um modelo está bloqueado agora por uma janela de indisponibilidade recorrente.</summary>
public static class ModelAvailability
{
    public static bool IsBlockedAt(IReadOnlyList<ModelAvailabilityWindow>? windows, DateTime at)
    {
        if (windows is not { Count: > 0 })
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(at);
        var today = at.DayOfWeek;
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);

        foreach (var window in windows)
        {
            if (!TimeOnly.TryParse(window.StartTime, out var start) || !TimeOnly.TryParse(window.EndTime, out var end))
            {
                continue;
            }

            var sameDayWindow = start < end;
            if (sameDayWindow)
            {
                if (window.DaysOfWeek.Contains(today) && time >= start && time < end)
                {
                    return true;
                }

                continue;
            }

            // Janela cruza a meia-noite: começa em um dia e termina no seguinte.
            var startedToday = window.DaysOfWeek.Contains(today) && time >= start;
            var continuesFromYesterday = window.DaysOfWeek.Contains(yesterday) && time < end;
            if (startedToday || continuesFromYesterday)
            {
                return true;
            }
        }

        return false;
    }
}
