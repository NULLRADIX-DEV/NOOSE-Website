using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IThreatScoreConfigService" />
public class ThreatScoreConfigService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : IThreatScoreConfigService
{
    private const string CacheKey = "bedrohung:konfig";

    public async Task<ThreatScoreConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out ThreatScoreConfiguration? cached) && cached is not null)
        {
            return cached;
        }
        var config = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, config, TimeSpan.FromMinutes(10));
        return config;
    }

    public Task<ThreatScoreConfiguration> GetEditableAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken); // always fresh

    private async Task<ThreatScoreConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var json = (await db.ThreatScoreConfigs
            .FirstOrDefaultAsync(k => k.Id == ThreatScoreConfig.GlobalId, cancellationToken))?.Json;
        if (string.IsNullOrWhiteSpace(json))
        {
            return ThreatScoreConfiguration.Default();
        }
        try
        {
            // Missing fields use defaults.
            var config = JsonSerializer.Deserialize<ThreatScoreConfiguration>(json, ThreatScoreService.JsonOptions)
                         ?? ThreatScoreConfiguration.Default();
            // a stored config that violates the invariants (e.g. caps not summing to 100) would skew scores → fall back
            Validate(config);
            return config;
        }
        catch (JsonException)
        {
            return ThreatScoreConfiguration.Default();
        }
        catch (InvalidOperationException)
        {
            return ThreatScoreConfiguration.Default();
        }
    }

    public async Task SaveAsync(ThreatScoreConfiguration config, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireThreatScoreAdministration(actor);
        Validate(config);

        var json = JsonSerializer.Serialize(config, ThreatScoreService.JsonOptions);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ThreatScoreConfigs.FirstOrDefaultAsync(k => k.Id == ThreatScoreConfig.GlobalId, cancellationToken);
        if (row is null)
        {
            db.ThreatScoreConfigs.Add(new ThreatScoreConfig { Id = ThreatScoreConfig.GlobalId, Json = json });
        }
        else
        {
            row.Json = json;
        }
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>Validate config invariants.</summary>
    public static void Validate(ThreatScoreConfiguration k)
    {
        // matches the client-side gate tolerance so a config that passes the UI never throws here
        const double tol = 1e-6;

        void Positive(double value, string name)
        {
            if (value <= 0) throw new InvalidOperationException($"{name} muss größer als 0 sein.");
        }
        void NotNegative(double value, string name)
        {
            if (value < 0) throw new InvalidOperationException($"{name} darf nicht negativ sein.");
        }
        void SumEqual(double sum, double should, string was)
        {
            if (Math.Abs(sum - should) > tol)
                throw new InvalidOperationException($"{was} müssen auf {should:0.##} summieren (aktuell {sum:0.##}).");
        }

        Positive(k.HalfLifeDays, "Halbwertszeit (Tage)");
        Positive(k.S1Denominator, "S1-Nenner");
        Positive(k.SizeDenominator, "Größen-Nenner");
        Positive(k.WeaponsDenominator, "Waffen-Nenner");
        Positive(k.InfraDenominator, "Infrastruktur-Nenner");
        Positive(k.S3Denominator, "S3-Nenner");
        Positive(k.S4Denominator, "S4-Nenner");
        Positive(k.P1Denominator, "P1-Nenner");
        Positive(k.PersonWeaponsDenominator, "Person-Waffen-Nenner");
        Positive(k.P3Denominator, "P3-Nenner");
        Positive(k.P4Denominator, "P4-Nenner");
        Positive(k.P5Denominator, "P5-Nenner");

        if (k.ConfidenceFreshDays <= 0) throw new InvalidOperationException("Konfidenz-Frische (Tage) muss größer als 0 sein.");
        if (k.TriageThreshold is < 0 or > 100) throw new InvalidOperationException("Triage-Schwelle muss zwischen 0 und 100 liegen.");
        NotNegative(k.AlarmDeltaThreshold, "Alarm-Delta-Schwelle");
        if (k.AlarmMinScore is < 0 or > 100) throw new InvalidOperationException("Alarm-Mindest-Score muss zwischen 0 und 100 liegen.");
        if (k.RanksMaxPoints < 0) throw new InvalidOperationException("Ränge-Max-Punkte darf nicht negativ sein.");

        // Severity tiers: monotone.
        if (!(k.KindWeightHeavy >= k.KindWeightMedium && k.KindWeightMedium >= k.KindWeightLight && k.KindWeightLight >= 1))
            throw new InvalidOperationException("Schwere-Gewichte müssen erfüllen: schwer ≥ mittel ≥ leicht ≥ 1.");
        NotNegative(k.OutcomeShot, "Ausgang Erschossen");
        NotNegative(k.OutcomeInjection, "Ausgang Spritze");
        NotNegative(k.OutcomeRunningStill, "Ausgang läuft noch");
        NotNegative(k.OutcomeReleased, "Ausgang entlassen");

        // Caps non-negative.
        foreach (var (value, name) in new (double, string)[]
        {
            (k.CapS1, "Cap S1"), (k.CapS2, "Cap S2"), (k.CapS3, "Cap S3"), (k.CapS4, "Cap S4"),
            (k.CapSize, "Cap Größe"), (k.LeadPoints, "Leitung-Punkte"), (k.EstatePoints, "Anwesen-Punkte"),
            (k.CapWeapons, "Cap Waffen"), (k.CapInfra, "Cap Infrastruktur"), (k.DrugRouteWeight, "Drogenroute-Gewicht"),
            (k.ConflictWeight, "Konflikt-Gewicht"), (k.AllianceWeight, "Bündnis-Gewicht"),
            (k.DocHeatWeight, "Dok-Heat-Gewicht"), (k.PerMemberDocCap, "Pro-Mitglied-Dok-Cap"),
            (k.CapP1, "Cap P1"), (k.CapP2, "Cap P2"), (k.CapP3, "Cap P3"), (k.CapP4, "Cap P4"), (k.CapP5, "Cap P5"),
            (k.PersonCapWeapons, "Person-Cap-Waffen"), (k.FugitivePoints, "Flüchtig-Punkte"),
            (k.ObservationCompletedWeight, "Observation-abgeschlossen-Gewicht"),
            (k.EnemyWeight, "Feind-Gewicht"), (k.AllyWeight, "Verbündeter-Gewicht"),
            (k.GpWeight, "Geschäftspartner-Gewicht"), (k.LeadWeight, "Leitungsrollen-Gewicht"),
        })
        {
            NotNegative(value, name);
        }

        // Caps must sum to 100.
        SumEqual(k.CapS1 + k.CapS2 + k.CapS3 + k.CapS4, 100, "Die Fraktion-Caps (S1–S4)");
        SumEqual(k.CapP1 + k.CapP2 + k.CapP3 + k.CapP4 + k.CapP5, 100, "Die Person-Caps (P1–P5)");
        // S2 sub-caps.
        SumEqual(k.CapSize + (k.RanksMaxPoints + k.LeadPoints + k.EstatePoints) + k.CapWeapons + k.CapInfra,
            k.CapS2, "Die S2-Sub-Caps (Größe + Struktur + Waffen + Infrastruktur)");
        // P2 sub-caps.
        SumEqual(k.PersonCapWeapons + k.FugitivePoints, k.CapP2, "Die P2-Sub-Caps (Waffen + Flüchtig)");
    }
}
