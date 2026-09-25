using System.ComponentModel.DataAnnotations;

namespace TakeOverBot.Models;

public class VoteParticipation
{
    [Key]
    public int Id { get; set; }

    [Required]
    public ulong PollMessageId { get; set; }

    [Required]
    public ulong ChannelId { get; set; }

    /// <summary>Salon du sondage : admin ou crew (vote-staff).</summary>
    [Required]
    [MaxLength(8)]
    public string Kind { get; set; } = string.Empty;

    [Required]
    public ulong UserId { get; set; }

    public bool Voted { get; set; }

    [Required]
    public long ClosedAt { get; set; }
}
