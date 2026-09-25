using System.ComponentModel.DataAnnotations;

namespace TakeOverBot.Models;

public class VoteRecapState
{
    [Key]
    public int Id { get; set; }

    [Required]
    public long PeriodStartedAt { get; set; }

    /// <summary>IDs des messages recap (ordre), séparés par des virgules.</summary>
    [MaxLength(256)]
    public string RecapMessageIdsCsv { get; set; } = string.Empty;
}
