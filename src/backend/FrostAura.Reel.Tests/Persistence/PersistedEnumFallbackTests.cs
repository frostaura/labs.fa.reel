using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
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

    [Fact]
    public void No_persisted_enum_is_a_flags_enum()
    {
        // Both readers accept only an exact member NAME; a combined flags value is written as
        // "A, B", which they would read as unknown. Declare a flags enum here on purpose or not at all.
        var flags = PersistedEnums().Select(e => e.EnumType)
            .Concat(JsonPersistedEnums().Select(e => e.EnumType))
            .Where(t => t.IsDefined(typeof(FlagsAttribute), inherit: false))
            .Distinct()
            .ToList();
        Assert.Empty(flags);
    }

    // ── Enums inside persisted JSON ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every type this build deserializes from a persisted JSON column (or a cookie that outlives a
    /// deploy). A new one belongs here; the walk below finds the enums inside it, so an enum later
    /// added to any of them needs a declared fallback before this test passes.
    /// </summary>
    private static readonly string[] JsonPersistedTypeNames =
    [
        "FrostAura.Reel.Domain.Ports.SearchIntent, FrostAura.Reel.Domain",
        "FrostAura.Reel.Domain.Lens.LensNote, FrostAura.Reel.Domain",
        "FrostAura.Reel.Domain.Lens.TasteFacts, FrostAura.Reel.Domain",
        "FrostAura.Reel.Domain.Feed.DigestItem, FrostAura.Reel.Domain",
        "FrostAura.Reel.Domain.Tenancy.AccountSettings, FrostAura.Reel.Domain",
        "FrostAura.Reel.Application.Sync.OutboxDispatcher+OutboxPayload, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Ranking.SharedEvidence, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Jobs.CatalogSeedCursor, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Jobs.DeltaSyncJobHandler+Cursor, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Jobs.FullIngestJobHandler+Cursor, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Jobs.HydrateCatalogJobHandler+Cursor, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Jobs.HydrateCatalogJobHandler+GlobalCursor, FrostAura.Reel.Application",
        "FrostAura.Reel.Application.Auth.OAuthStateCodec+StatePayload, FrostAura.Reel.Application",
        "FrostAura.Reel.Infrastructure.Ml.FastTreeModelEngine+Hyperparams, FrostAura.Reel.Infrastructure",
        "FrostAura.Reel.Api.Endpoints.TitleEndpoints+ContributionEntry, FrostAura.Reel.Api",
    ];

    /// <summary>
    /// JSON-persisted enum properties that still read through a converter that throws on an unknown
    /// name, each with the change request that moves it. The guard below fails when a new one
    /// appears, and when one listed here has been moved (so the entry is deleted, not left stale).
    /// </summary>
    private static readonly Dictionary<string, string> PendingAdoption = new(StringComparer.Ordinal)
    {
        ["OutboxPayload.MediaType"] =
            "WS-4 (R1-43 CR): UnrecognisedJsonEnumConverter<MediaType>, and send only an explicit Movie or Show",
    };

    private static IReadOnlyList<(string Path, Type EnumType, PropertyInfo? Property)> JsonPersistedEnums()
    {
        var found = new List<(string, Type, PropertyInfo?)>();
        foreach (var name in JsonPersistedTypeNames)
        {
            var type = Type.GetType(name, throwOnError: true)!;
            Walk(type, type.Name, null, found, []);
        }

        return found;
    }

    private static void Walk(Type type, string path, PropertyInfo? property, List<(string, Type, PropertyInfo?)> found, HashSet<Type> seen)
    {
        var plain = Nullable.GetUnderlyingType(type) ?? type;
        if (plain.IsEnum)
        {
            found.Add((path, plain, property));
            return;
        }

        if (plain.IsPrimitive || plain == typeof(string) || plain == typeof(decimal) || plain == typeof(Guid)
            || plain == typeof(DateTime) || plain == typeof(DateTimeOffset) || plain == typeof(JsonElement)
            || !seen.Add(plain))
        {
            return;
        }

        if (plain.IsArray)
        {
            Walk(plain.GetElementType()!, path + "[]", property, found, seen);
            return;
        }

        if (plain.IsGenericType && plain.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            foreach (var argument in plain.GetGenericArguments())
            {
                Walk(argument, path + "[]", property, found, seen);
            }

            return;
        }

        foreach (var member in plain.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Walk(member.PropertyType, $"{plain.Name}.{member.Name}", member, found, seen);
        }
    }

    public static TheoryData<Type> AllPersistedEnumTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in PersistedEnums().Select(e => e.EnumType).Concat(JsonPersistedEnums().Select(e => e.EnumType)).Distinct())
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void Persisted_json_carries_enums_at_all()
    {
        // Guards the walk against passing vacuously: these are the two known today.
        var paths = JsonPersistedEnums().Select(e => e.Path).ToList();
        Assert.Contains("SearchIntent.MediaTypes[]", paths);
        Assert.Contains("PersonQuery.Role", paths);
        Assert.Contains("OutboxPayload.MediaType", paths);
    }

    [Fact]
    public void Every_enum_inside_persisted_json_has_a_fallback_the_schema_steward_declared()
    {
        var undeclared = JsonPersistedEnums()
            .Where(e => !PersistedEnumFallbacks.IsDeclared(e.EnumType))
            .Select(e => $"{e.Path} ({e.EnumType.Name}): add it to PersistedEnumFallbacks with its reason")
            .ToList();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void Every_annotated_json_enum_reads_through_a_tolerant_converter_or_is_pending_by_name()
    {
        var tolerant = new[] { typeof(TolerantJsonEnumConverter<>), typeof(UnrecognisedJsonEnumConverter<>) };
        var annotated = JsonPersistedEnums()
            .Where(e => e.Property?.GetCustomAttribute<JsonConverterAttribute>() is not null)
            .Select(e => (e.Path, Converter: e.Property!.GetCustomAttribute<JsonConverterAttribute>()!.ConverterType))
            .ToList();
        var intolerant = annotated
            .Where(a => a.Converter is null || !a.Converter.IsGenericType || !tolerant.Contains(a.Converter.GetGenericTypeDefinition()))
            .Select(a => a.Path)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(intolerant.Except(PendingAdoption.Keys));
        Assert.True(
            PendingAdoption.Keys.All(intolerant.Contains),
            "Adopted, so delete from PendingAdoption: " + string.Join(", ", PendingAdoption.Keys.Except(intolerant)));
    }

    [Theory]
    [MemberData(nameof(AllPersistedEnumTypes))]
    public void In_json_every_member_round_trips_by_name_and_reads_from_the_number_the_default_options_wrote(Type enumType) =>
        Generic(nameof(JsonRoundTrips), enumType);

    [Theory]
    [MemberData(nameof(AllPersistedEnumTypes))]
    public void In_json_an_unknown_name_number_or_token_reads_as_the_declared_fallback(Type enumType) =>
        Generic(nameof(JsonFallsBack), enumType);

    [Fact]
    public void A_stored_intent_with_media_types_and_roles_from_a_newer_build_reads_with_its_known_values_intact()
    {
        // Written exactly as LiveSearchExpansionService writes SearchTurns.IntentJson: default
        // options, so enums are numbers. Then what a newer build with more members would have added.
        var stored = JsonSerializer.Serialize(new SearchIntent(
            ["Drama"], [], [MediaType.Show], "slow burn", null, null,
            People: [new PersonQuery("Denis Villeneuve", PersonRole.Director)]));
        var newer = JsonNode.Parse(stored)!;
        newer["MediaTypes"]!.AsArray().Add(7);
        newer["MediaTypes"]!.AsArray().Add("Documentary");
        newer["People"]!.AsArray().Add(new JsonObject { ["Name"] = "Hans Zimmer", ["Role"] = "Composer" });
        newer["People"]!.AsArray().Add(new JsonObject { ["Name"] = "Roger Deakins", ["Role"] = 9 });

        var read = JsonSerializer.Deserialize<SearchIntent>(newer.ToJsonString(), PersistedJson.Options)!;
        Assert.Equal([MediaType.Show, MediaType.Movie, MediaType.Movie], read.MediaTypes);
        Assert.Equal([PersonRole.Director, PersonRole.Any, PersonRole.Any], read.People!.Select(p => p.Role));
        Assert.Equal("slow burn", read.FreeText);

        // Why the options are needed: the default reader throws on the name and casts the number
        // into a value no member has.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SearchIntent>(newer.ToJsonString()));
        newer["MediaTypes"]!.AsArray().RemoveAt(2);
        newer["People"]!.AsArray().RemoveAt(1);
        var unguarded = JsonSerializer.Deserialize<SearchIntent>(newer.ToJsonString())!;
        Assert.False(Enum.IsDefined(unguarded.MediaTypes[1]));
    }

    /// <summary>The outbox payload's shape with the converter WS-4 is asked to adopt.</summary>
    private sealed record OutboxShape(
        Guid TitleId,
        [property: JsonConverter(typeof(UnrecognisedJsonEnumConverter<MediaType>))] MediaType MediaType,
        long? TmdbId,
        long? TraktId,
        short? Rating,
        DateTime? WatchedAt);

    [Fact]
    public void An_outbox_payload_from_a_newer_build_reads_as_no_media_type_so_no_trakt_write_can_be_built_from_it()
    {
        var titleId = Guid.NewGuid();
        var current = JsonSerializer.Serialize(new OutboxDispatcher.OutboxPayload(titleId, MediaType.Show, 1399, 1390, 9, null));

        // Every payload the current writer produces reads the same through the tolerant shape.
        var same = JsonSerializer.Deserialize<OutboxShape>(current)!;
        Assert.Equal(MediaType.Show, same.MediaType);
        Assert.Equal(titleId, same.TitleId);
        Assert.Equal(current, JsonSerializer.Serialize(same));

        var newer = current.Replace("\"Show\"", "\"Episode\"", StringComparison.Ordinal);

        // Today: the whole kind's batch fails to deserialize on one entry (CR to WS-4).
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutboxDispatcher.OutboxPayload>(newer));

        // Adopted: the entry reads, and is neither a movie nor a show, so the dispatcher's
        // Movie/Show partition sends it nowhere. The declared fallback (Movie) would have sent a
        // show's id to Trakt's movie endpoint — why this converter is not the table's.
        var read = JsonSerializer.Deserialize<OutboxShape>(newer)!;
        Assert.NotEqual(MediaType.Movie, read.MediaType);
        Assert.NotEqual(MediaType.Show, read.MediaType);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(read));
    }

    private static void JsonRoundTrips<TEnum>()
        where TEnum : struct, Enum
    {
        var options = PersistedJson.Options;
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var written = JsonSerializer.Serialize(value, options);
            Assert.Equal(JsonSerializer.Serialize(value, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }), written);
            Assert.Equal(value, JsonSerializer.Deserialize<TEnum>(written, options));
            Assert.Equal(value, JsonSerializer.Deserialize<TEnum>(JsonSerializer.Serialize(value), options));
            Assert.Equal(value, JsonSerializer.Deserialize<TEnum>(written, Unrecognised<TEnum>()));
        }
    }

    private static void JsonFallsBack<TEnum>()
        where TEnum : struct, Enum
    {
        var fallback = PersistedEnumFallbacks.For<TEnum>();
        var unrecognised = PersistedEnumFallbacks.Unrecognised<TEnum>();
        var undefinedNumber = Enum.GetValues<TEnum>().Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).Max() + 1;
        var anyMember = Enum.GetNames<TEnum>()[0];
        string[] unknown =
        [
            "\"NotAMemberOfThisBuild\"",
            undefinedNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-1",
            "1.5",
            "\"\"",
            "\"1\"",
            $"\" {anyMember}\"",
            $"\"{anyMember.ToUpperInvariant()}{(anyMember.ToUpperInvariant() == anyMember ? "x" : string.Empty)}\"",
            $"\"{anyMember}, {anyMember}\"",
            "null",
            "true",
            "{\"a\":1}",
            "[1,2]",
        ];

        foreach (var json in unknown)
        {
            Assert.Equal(fallback, JsonSerializer.Deserialize<TEnum>(json, PersistedJson.Options));
            Assert.Equal(unrecognised, JsonSerializer.Deserialize<TEnum>(json, Unrecognised<TEnum>()));
        }

        // Inside a document, a skipped object or array leaves the reader on the next property.
        var wrapped = JsonSerializer.Deserialize<Dictionary<string, TEnum>>(
            $"{{\"a\":{{\"x\":[1]}},\"b\":\"{anyMember}\"}}", PersistedJson.Options)!;
        Assert.Equal(fallback, wrapped["a"]);
        Assert.Equal(Enum.Parse<TEnum>(anyMember), wrapped["b"]);

        // A nullable enum keeps null as null; only a value it cannot name falls back.
        Assert.Null(JsonSerializer.Deserialize<TEnum?>("null", PersistedJson.Options));
        Assert.Equal(fallback, JsonSerializer.Deserialize<TEnum?>("\"NotAMemberOfThisBuild\"", PersistedJson.Options));
    }

    private static JsonSerializerOptions Unrecognised<TEnum>()
        where TEnum : struct, Enum =>
        new() { Converters = { new UnrecognisedJsonEnumConverter<TEnum>() } };

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
