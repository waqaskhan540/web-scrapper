using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace CarIngest.Infrastructure.Storage;

public sealed class CarIngestDbContext(DbContextOptions<CarIngestDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// Vector width. Must match CarIngest:Embedding:Dimensions; startup validation caps it at
    /// 2000 because an indexed pgvector column cannot exceed that (research D4). Changing it
    /// means a new table via migration, not a column alteration.
    /// </summary>
    public const int EmbeddingDimensions = 768;

    public DbSet<AddressSource> AddressSources => this.Set<AddressSource>();

    public DbSet<IngestionTarget> IngestionTargets => this.Set<IngestionTarget>();

    public DbSet<CarDocument> CarDocuments => this.Set<CarDocument>();

    public DbSet<CarDocumentEmbedding> CarDocumentEmbeddings => this.Set<CarDocumentEmbedding>();

    public DbSet<IngestionRun> IngestionRuns => this.Set<IngestionRun>();

    public DbSet<AddressOutcome> AddressOutcomes => this.Set<AddressOutcome>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        // Emits CREATE EXTENSION IF NOT EXISTS "vector" into the migration, so the extension is
        // created by checked-in SQL rather than inferred at runtime (gate III-3).
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<AddressSource>(e =>
        {
            e.ToTable("address_source");
            e.HasKey(x => x.Id);
            e.Property(x => x.Location).IsRequired();
            e.HasIndex(x => x.Location).IsUnique();
        });

        modelBuilder.Entity<IngestionTarget>(e =>
        {
            e.ToTable("ingestion_target");
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).IsRequired();
            e.Property(x => x.Host).IsRequired();

            // Text, not an int-backed enum: a future crawling feature must be able to add a
            // value without a data migration (FR-015a).
            e.Property(x => x.DiscoveryMethod).IsRequired().HasMaxLength(64);
            e.Property(x => x.FirstSeenAt).IsRequired();

            e.HasIndex(x => x.Url).IsUnique();
            e.HasIndex(x => x.Host);

            e.HasOne(x => x.AddressSource)
             .WithMany(x => x.Targets)
             .HasForeignKey(x => x.AddressSourceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CarDocument>(e =>
        {
            e.ToTable("car_document", t =>
            {
                // Sanity ranges from data-model.md §4. Enforced in the database as well as in
                // the extractor, so a future code path cannot write nonsense by bypassing
                // validation.
                t.HasCheckConstraint("ck_car_document_year", "year BETWEEN 1900 AND 2100");
                t.HasCheckConstraint("ck_car_document_mileage", "mileage_value IS NULL OR (mileage_value BETWEEN 0 AND 1000000)");
                t.HasCheckConstraint("ck_car_document_price", "price_amount IS NULL OR (price_amount BETWEEN 0 AND 10000000)");
                t.HasCheckConstraint("ck_car_document_mileage_unit", "mileage_value IS NULL OR mileage_unit IS NOT NULL");
                t.HasCheckConstraint("ck_car_document_price_currency", "price_amount IS NULL OR price_currency IS NOT NULL");
                t.HasCheckConstraint("ck_car_document_availability", "availability IN ('available','unavailable')");
            });

            e.HasKey(x => x.Id);

            e.Property(x => x.Make).IsRequired();
            e.Property(x => x.Model).IsRequired();
            e.Property(x => x.Year).IsRequired();
            e.Property(x => x.PriceAmount).HasPrecision(12, 2);
            e.Property(x => x.PriceCurrency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.MileageUnit).HasMaxLength(2);
            e.Property(x => x.Vin).HasMaxLength(17);
            e.Property(x => x.Availability).IsRequired().HasMaxLength(16);
            e.Property(x => x.ContentHash).IsRequired();

            // Provenance is non-nullable in full: a row that cannot say where it came from
            // cannot be written at all (FR-015, SC-004).
            e.Property(x => x.SourceUrl).IsRequired();
            e.Property(x => x.RetrievedAt).IsRequired();
            e.Property(x => x.ExtractorVersion).IsRequired().HasMaxLength(128);
            e.Property(x => x.DiscoveryMethod).IsRequired().HasMaxLength(64);
            e.Property(x => x.LastVerifiedAt).IsRequired();

            e.Property(x => x.CardKey).IsRequired().HasMaxLength(256);

            // One document per (address, card). A listing page describes several cars, so the
            // address alone cannot identify a document.
            e.HasIndex(x => new { x.IngestionTargetId, x.CardKey }).IsUnique();

            e.HasOne(x => x.IngestionTarget)
             .WithMany(x => x.Documents)
             .HasForeignKey(x => x.IngestionTargetId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CarDocumentEmbedding>(e =>
        {
            e.ToTable("car_document_embedding");
            e.HasKey(x => x.Id);

            // Unindexed on purpose: exact scan is ample at tens of thousands of rows, and an
            // HNSW index would add build cost and recall tuning for no measurable gain
            // (research D4). Revisit only if the measured latency goal is missed.
            e.Property(x => x.Embedding)
             .HasColumnType($"vector({EmbeddingDimensions})")
             .IsRequired();

            e.Property(x => x.ModelId).IsRequired().HasMaxLength(128);
            e.Property(x => x.ModelVersion).IsRequired().HasMaxLength(128);
            e.Property(x => x.EmbeddedAt).IsRequired();
            e.Property(x => x.SourceTextHash).IsRequired();

            // One vector per document per model identity.
            e.HasIndex(x => new { x.CarDocumentId, x.ModelId, x.ModelVersion }).IsUnique();

            // Makes the mandatory model-identity filter cheap (FR-029).
            e.HasIndex(x => new { x.ModelId, x.ModelVersion });

            e.HasOne(x => x.CarDocument)
             .WithMany(x => x.Embeddings)
             .HasForeignKey(x => x.CarDocumentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestionRun>(e =>
        {
            e.ToTable("ingestion_run", t => t.HasCheckConstraint(
                "ck_ingestion_run_status",
                "status IN ('running','succeeded','failed','interrupted')"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Status).IsRequired().HasMaxLength(16);
            e.Property(x => x.TriggeredBy).IsRequired().HasMaxLength(16);
            e.Property(x => x.StartedAt).IsRequired();
            e.Property(x => x.FilesRead).HasColumnType("text[]");

            // At most one active run per source. A partial unique index, so the constraint is
            // enforced by the database rather than by a racy application check (FR-026).
            e.HasIndex(x => x.AddressSourceId)
             .IsUnique()
             .HasFilter("status = 'running'")
             .HasDatabaseName("ux_ingestion_run_one_active_per_source");

            e.HasOne(x => x.AddressSource)
             .WithMany()
             .HasForeignKey(x => x.AddressSourceId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<IngestionRun>()
             .WithMany()
             .HasForeignKey(x => x.RetryOfRunId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AddressOutcome>(e =>
        {
            e.ToTable("address_outcome", t => t.HasCheckConstraint(
                "ck_address_outcome_kind",
                "outcome IN ('stored','unchanged','skipped-by-policy','failed')"));

            e.HasKey(x => x.Id);
            e.Property(x => x.Outcome).IsRequired().HasMaxLength(24);
            e.Property(x => x.ReasonCode).HasMaxLength(48);
            e.Property(x => x.RecordedAt).IsRequired();

            // Exactly one outcome per address per run — what makes SC-007 checkable.
            e.HasIndex(x => new { x.IngestionRunId, x.IngestionTargetId }).IsUnique();

            e.HasOne(x => x.IngestionRun)
             .WithMany(x => x.Outcomes)
             .HasForeignKey(x => x.IngestionRunId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.IngestionTarget)
             .WithMany()
             .HasForeignKey(x => x.IngestionTargetId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Snake-case every column so the check constraints above reference real names.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                builder.Append(name[i]);
            }
        }

        return builder.ToString();
    }
}
