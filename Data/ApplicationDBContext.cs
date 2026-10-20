using Microsoft.EntityFrameworkCore;

internal class ApplicationDBContext : DbContext
{
    public ApplicationDBContext(
        DbContextOptions<ApplicationDBContext> options)
        : base(options)
    {
    }

    //public DbSet<Omni_Fintech_TankMapping> Tanks => Set<Omni_Fintech_TankMapping>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure relationships here
    }
}
