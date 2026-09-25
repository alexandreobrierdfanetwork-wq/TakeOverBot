using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TakeOverBot.Models;

namespace TakeOverBot;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<LastPost> LastPosts => Set<LastPost>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<VotePoll> VotePolls => Set<VotePoll>();
    public DbSet<PendingPost> PendingPosts => Set<PendingPost>();
    public DbSet<VoteParticipation> VoteParticipations => Set<VoteParticipation>();
    public DbSet<VoteRecapState> VoteRecapStates => Set<VoteRecapState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VotePoll>()
            .HasIndex(p => p.MessageId)
            .IsUnique();

        modelBuilder.Entity<VoteParticipation>()
            .HasIndex(p => new { p.PollMessageId, p.UserId })
            .IsUnique();

        modelBuilder.Entity<VoteParticipation>()
            .HasIndex(p => p.ClosedAt);
    }
}

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable("APP_DATABASE_PATH") ?? "Data/app.db";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new AppDbContext(options);
    }
}