using System.ComponentModel.DataAnnotations;

namespace TakeOverBot.Models;

public class VotePoll
{
    [Key]
    public int Id { get; set; }

    [Required]
    public ulong GuildId { get; set; }

    [Required]
    public ulong ChannelId { get; set; }

    [Required]
    public ulong MessageId { get; set; }

    public ulong TargetRoleId { get; set; }

    [Required]
    public long CreatedAt { get; set; }

    [Required]
    public long ExpiresAt { get; set; }

    public bool Remind72Sent { get; set; }

    public bool Remind24Sent { get; set; }

    public bool Remind3Sent { get; set; }
}
