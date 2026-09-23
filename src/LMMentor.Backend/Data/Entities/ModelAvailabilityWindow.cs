namespace LMMentor.Backend.Data.Entities;

/// <summary>
/// Janela recorrente em que um modelo fica indisponível (ex.: rush hour de um provedor).
/// Horários são hora local do servidor, no formato "HH:mm".
/// </summary>
public sealed class ModelAvailabilityWindow
{
    /// <summary>Dias da semana em que a janela se aplica (Sunday=0 .. Saturday=6).</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();

    public string StartTime { get; set; } = "00:00";

    /// <summary>Se menor ou igual a StartTime, a janela cruza a meia-noite (termina no dia seguinte).</summary>
    public string EndTime { get; set; } = "00:00";
}
