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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VotePoll>()
            .HasIndex(p => p.MessageId)
            .IsUnique();
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