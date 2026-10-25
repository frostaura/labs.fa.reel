using System.Reflection;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FrostAura.Reel.Tests.Persistence;

/// <summary>
/// Program §5.0 rule 8, the reader half: every persisted enum reads a value this build does not
/// define as a declared fallback instead of throwing, so an image can be rolled back by tag after a
/// newer one wrote a member it has never heard of. Offline — the model and the converters only.
/// </summary>
public class PersistedEnumFallbackTests
{
    private static ReelDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only", npgsql => npgsql.UseVector())
                .Options,
            new AccountContext());

    private static IReadOnlyList<(string Name, Type EnumType, ValueConverter? Converter)> PersistedEnums()
    {
        using var db = CreateContext();
        return db.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Select(p => (Entity: t, Property: p)))
            .Select(x => (x.Entity, x.Property, Enum: Nullable.GetUnderlyingType(x.Property.ClrType) ?? x.Property.ClrType))
            .Where(x => x.Enum.IsEnum)
            .Select(x => ($"{x.Entity.ClrType.Name}.{x.Property.Name}", x.Enum, x.Property.GetTypeMapping().Converter))
            .ToList();
    }

    public static TheoryData<Type> PersistedEnumTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in PersistedEnums().Select(e => e.EnumType).Distinct())
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void The_model_persists_enums_at_all()
    {
        // Guards the theories below against passing vacuously on an empty walk.
        Assert.Contains(PersistedEnums(), e => e.EnumType == typeof(JobStatus));
        Assert.Contains(PersistedEnums(), e => e.EnumType == typeof(PipelineStage));
    }

    [Fact]
    public void Every_persisted_enum_property_reads_through_the_tolerant_converter()
    {
        var wrong = PersistedEnums()
            .Where(e => e.Converter?.GetType() != typeof(TolerantEnumConverter<>).MakeGenericType(e.EnumType))
            .Select(e => $"{e.Name} ({e.Converter?.GetType().Name ?? "no converter"})")
            .ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_persisted_enum_has_a_fallback_the_schema_steward_declared()
    {
        var undeclared = PersistedEnums()
            .Where(e => !PersistedEnumFallbacks.IsDeclared(e.EnumType))
            .Select(e => $"{e.Name} ({e.EnumType.Name}): add it to PersistedEnumFallbacks with its reason")
            .Distinct()
            .ToList();

        Assert.Empty(undeclared);
    }

    [Theory]
    [MemberData(nameof(PersistedEnumTypes))]
    public void Every_member_round_trips_as_the_same_text_the_built_in_converter_wrote(Type enumType) =>
        Generic(nameof(RoundTrips), enumType);

    [Theory]
    [MemberData(nameof(PersistedEnumTypes))]
    public void An_unknown_name_a_number_or_a_differently_cased_name_reads_as_the_declared_fallback(Type enumType) =>
        Generic(nameof(FallsBack), enumType);

    [Fact]
    public void The_rollback_case_that_motivated_this_reads_as_degraded_on_a_build_without_it()
    {
        // NotEnoughRatings was the first member a newer image wrote that an older one could not read.
        Assert.Equal(PipelineStage.Degraded, TolerantEnumConverter<PipelineStage>.Read("SomeStageAddedLater"));
        Assert.Equal(PipelineStage.NotEnoughRatings, TolerantEnumConverter<PipelineStage>.Read("NotEnoughRatings"));
    }

    [Fact]
    public void An_unrecognised_outbox_kind_is_no_member_at_all_so_no_trakt_write_is_made_from_it()
    {
        var read = TolerantEnumConverter<OutboxKind>.Read("RemoveFromCollection");
        Assert.False(Enum.IsDefined(read));
    }

    [Fact]
    public void Unknown_job_rows_read_as_inert_terminal_background_work()
    {
        Assert.Equal(JobStatus.Cancelled, TolerantEnumConverter<JobStatus>.Read("Paused"));
        Assert.Equal(JobKind.SeedCatalog, TolerantEnumConverter<JobKind>.Read("RepairAvailability"));
    }

    private static void RoundTrips<TEnum>()
        where TEnum : struct, Enum
    {
        var tolerant = new TolerantEnumConverter<TEnum>();
        var builtIn = new EnumToStringConverter<TEnum>();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var written = (string)tolerant.ConvertToProvider(value)!;
            Assert.Equal((string)builtIn.ConvertToProvider(value)!, written);
            Assert.Equal(value, (TEnum)tolerant.ConvertFromProvider(written)!);
        }
    }

    private static void FallsBack<TEnum>()
        where TEnum : struct, Enum
    {
        var fallback = PersistedEnumFallbacks.For<TEnum>();
        var converter = new TolerantEnumConverter<TEnum>();
        var anyMember = Enum.GetNames<TEnum>()[0];

        Assert.Equal(fallback, (TEnum)converter.ConvertFromProvider("NotAMemberOfThisBuild")!);
        Assert.Equal(fallback, (TEnum)converter.ConvertFromProvider("1")!);
        Assert.Equal(fallback, (TEnum)converter.ConvertFromProvider("-1")!);
        Assert.Equal(fallback, (TEnum)converter.ConvertFromProvider(string.Empty)!);
        if (!string.Equals(anyMember.ToUpperInvariant(), anyMember, StringComparison.Ordinal))
        {
            Assert.Equal(fallback, (TEnum)converter.ConvertFromProvider(anyMember.ToUpperInvariant())!);
        }

        // The fallback is a member, or the deliberate out-of-range sentinel — never an accident.
        Assert.True(Enum.IsDefined(fallback) || fallback.Equals(PersistedEnumFallbacks.Unrecognised<TEnum>()));
    }

    private static void Generic(string method, Type enumType) =>
        typeof(PersistedEnumFallbackTests)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(enumType)
            .Invoke(null, null);
}
