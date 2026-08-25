using Microsoft.EntityFrameworkCore;
using ProfileSvr.Domain;

namespace ProfileSvr.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<Activity> Activities => Set<Activity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Profile>(entity =>
        {
            entity.ToTable("profiles");

            entity.HasKey(p => p.Id);

            entity.Property(p => p.Id)
                .HasColumnName("id");

            entity.Property(p => p.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(ProfileStatus.AuthCreated);

            entity.Property(p => p.EmailAddress)
                .HasColumnName("email_address")
                .HasMaxLength(320)
                .IsRequired();

            entity.Property(p => p.PhoneNumber)
                .HasColumnName("phone_number")
                .HasMaxLength(32);

            entity.Property(p => p.EmailConfirmed)
                .HasColumnName("email_confirmed")
                .HasDefaultValue(false);

            entity.Property(p => p.PhoneNumberConfirmed)
                .HasColumnName("phone_number_confirmed")
                .HasDefaultValue(false);

            entity.Property(p => p.CreatedAtUtc)
                .HasColumnName("created_at_utc");

            entity.Property(p => p.UpdatedAtUtc)
                .HasColumnName("updated_at_utc");

            entity.Property(p => p.DeviceId)
                .HasColumnName("device_id");

            entity.Property(p => p.TransactionPinSalt)
                .HasColumnName("transaction_pin_salt")
                .HasMaxLength(64);

            entity.Property(p => p.TransactionPinHash)
                .HasColumnName("transaction_pin_hash")
                .HasMaxLength(128);

            entity.Property(p => p.HasSetTransactionPin)
                .HasColumnName("has_set_transaction_pin")
                .HasDefaultValue(false);

            entity.Property(p => p.DeviceChangedAtUtc)
                .HasColumnName("device_changed_at_utc");

            entity.Property(p => p.FirstName)
                .HasColumnName("first_name")
                .HasMaxLength(100);

            entity.Property(p => p.LastName)
                .HasColumnName("last_name")
                .HasMaxLength(100);

            entity.Property(p => p.MiddleName)
                .HasColumnName("middle_name")
                .HasMaxLength(100);

            entity.Property(p => p.DateOfBirth)
                .HasColumnName("date_of_birth");

            entity.Property(p => p.Gender)
                .HasColumnName("gender")
                .HasMaxLength(16);

            entity.Property(p => p.Tier)
                .HasColumnName("tier")
                .HasDefaultValue(0);

            entity.Property(p => p.Cif)
                .HasColumnName("cif")
                .HasMaxLength(64);

            entity.Property(p => p.Address)
                .HasColumnName("address")
                .HasMaxLength(256);

            entity.Property(p => p.Bvn)
                .HasColumnName("bvn")
                .HasMaxLength(11);

            entity.Property(p => p.Nin)
                .HasColumnName("nin")
                .HasMaxLength(11);

            entity.Property(p => p.BvnIsVerified)
                .HasColumnName("bvn_is_verified")
                .HasDefaultValue(false);

            entity.Property(p => p.NinIsVerified)
                .HasColumnName("nin_is_verified")
                .HasDefaultValue(false);

            entity.HasIndex(p => p.EmailAddress).IsUnique();
            entity.HasIndex(p => p.PhoneNumber).IsUnique();

            entity.HasOne(p => p.Device)
                .WithMany(d => d.Profiles)
                .HasForeignKey(p => p.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");

            entity.HasKey(d => d.Id);

            entity.Property(d => d.Id)
                .HasColumnName("id");

            entity.Property(d => d.DeviceIdentifier)
                .HasColumnName("device_identifier")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(d => d.Name)
                .HasColumnName("name")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(d => d.Platform)
                .HasColumnName("platform")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(d => d.OsName)
                .HasColumnName("os_name")
                .HasMaxLength(64);

            entity.Property(d => d.OsVersion)
                .HasColumnName("os_version")
                .HasMaxLength(64);

            entity.Property(d => d.Manufacturer)
                .HasColumnName("manufacturer")
                .HasMaxLength(128);

            entity.Property(d => d.Model)
                .HasColumnName("model")
                .HasMaxLength(128);

            entity.Property(d => d.AppVersion)
                .HasColumnName("app_version")
                .HasMaxLength(32);

            entity.Property(d => d.CreatedAtUtc)
                .HasColumnName("created_at_utc");

            entity.Property(d => d.UpdatedAtUtc)
                .HasColumnName("updated_at_utc");

            entity.HasIndex(d => d.DeviceIdentifier).IsUnique();
        });

        modelBuilder.Entity<OtpCode>(entity =>
        {
            entity.ToTable("otp_codes");

            entity.HasKey(o => o.Id);

            entity.Property(o => o.Id)
                .HasColumnName("id");

            entity.Property(o => o.RetrievalCode)
                .HasColumnName("retrieval_code")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(o => o.Purpose)
                .HasColumnName("purpose")
                .HasConversion<string>()
                .HasMaxLength(24);

            entity.Property(o => o.ProfileId)
                .HasColumnName("profile_id");

            entity.Property(o => o.SourceId)
                .HasColumnName("source_id");

            entity.Property(o => o.DeviceId)
                .HasColumnName("device_id");

            entity.Property(o => o.Target)
                .HasColumnName("target")
                .HasMaxLength(320)
                .IsRequired();

            entity.Property(o => o.Channel)
                .HasColumnName("channel")
                .HasConversion<string>()
                .HasMaxLength(16);

            entity.Property(o => o.CodeHash)
                .HasColumnName("code_hash")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(o => o.Attempts)
                .HasColumnName("attempts")
                .HasDefaultValue(0);

            entity.Property(o => o.ExpiresAtUtc)
                .HasColumnName("expires_at_utc");

            entity.Property(o => o.ConsumedAtUtc)
                .HasColumnName("consumed_at_utc");

            entity.Property(o => o.CreatedAtUtc)
                .HasColumnName("created_at_utc");

            entity.HasIndex(o => o.RetrievalCode).IsUnique();
            entity.HasIndex(o => new { o.ProfileId, o.Channel });
            entity.HasIndex(o => new { o.Target, o.Purpose });

            entity.HasOne(o => o.Profile)
                .WithMany()
                .HasForeignKey(o => o.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(o => o.Device)
                .WithMany()
                .HasForeignKey(o => o.DeviceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.ToTable("user_devices");

            entity.HasKey(ud => ud.Id);

            entity.Property(ud => ud.Id)
                .HasColumnName("id");

            entity.Property(ud => ud.ProfileId)
                .HasColumnName("profile_id");

            entity.Property(ud => ud.DeviceId)
                .HasColumnName("device_id");

            entity.Property(ud => ud.LinkedAtUtc)
                .HasColumnName("linked_at_utc");

            entity.Property(ud => ud.ReleasedAtUtc)
                .HasColumnName("released_at_utc");

            entity.HasIndex(ud => new { ud.ProfileId, ud.ReleasedAtUtc });
            entity.HasIndex(ud => new { ud.DeviceId, ud.ReleasedAtUtc });

            entity.HasOne(ud => ud.Profile)
                .WithMany()
                .HasForeignKey(ud => ud.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ud => ud.Device)
                .WithMany()
                .HasForeignKey(ud => ud.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Activity>(entity =>
        {
            entity.ToTable("activities");

            entity.HasKey(a => a.Id);

            entity.Property(a => a.Id)
                .HasColumnName("id");

            entity.Property(a => a.ProfileId)
                .HasColumnName("profile_id");

            entity.Property(a => a.DeviceId)
                .HasColumnName("device_id");

            entity.Property(a => a.Type)
                .HasColumnName("type")
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(a => a.Description)
                .HasColumnName("description")
                .HasMaxLength(256);

            entity.Property(a => a.IpAddress)
                .HasColumnName("ip_address")
                .HasMaxLength(45);

            entity.Property(a => a.CreatedAtUtc)
                .HasColumnName("created_at_utc");

            entity.HasIndex(a => new { a.ProfileId, a.CreatedAtUtc });

            entity.HasOne(a => a.Profile)
                .WithMany()
                .HasForeignKey(a => a.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Device)
                .WithMany()
                .HasForeignKey(a => a.DeviceId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
