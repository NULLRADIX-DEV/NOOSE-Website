using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Operations;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>The sweep that stamps alarms past their expiry as closed.</summary>
public sealed class EmergencyAlarmExpiryWorkerTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static EmergencyAlarm Alarm(string id, DateTime expiry, Action<EmergencyAlarm>? configure = null)
    {
        var alarm = new EmergencyAlarm
        {
            Id = id,
            Situation = "Geiselnahme",
            TriggeredById = "trigger",
            TriggeredByName = "Codename-trigger",
            TriggeredAt = expiry - EmergencyAlarmRules.Duration,
            Expiry = expiry,
            CreatedAt = expiry - EmergencyAlarmRules.Duration,
        };
        configure?.Invoke(alarm);
        return alarm;
    }

    private static void Store(SqliteTestContext ctx, params EmergencyAlarm[] alarms)
    {
        using var db = ctx.NewContext();
        db.EmergencyAlarms.AddRange(alarms);
        db.SaveChanges();
    }

    private static EmergencyAlarm Read(SqliteTestContext ctx, string id)
    {
        using var db = ctx.NewContext();
        return db.EmergencyAlarms.AsNoTracking().Single(a => a.Id == id);
    }

    private static (EmergencyAlarmBroadcaster Broadcaster, List<string> Reports) Broadcast()
    {
        var broadcaster = new EmergencyAlarmBroadcaster();
        var reports = new List<string>();
        broadcaster.Modified += reports.Add;
        return (broadcaster, reports);
    }

    [Fact]
    public async Task Sweep_ClosesOnlyExpired()
    {
        using var ctx = new SqliteTestContext();
        var expiry = Now.AddMinutes(-1);
        Store(ctx, Alarm("gone", expiry), Alarm("running", Now.AddMinutes(10)));
        var (broadcaster, reports) = Broadcast();

        await using var db = ctx.NewContext();
        var closed = await EmergencyAlarmExpiryWorker.SweepAsync(db, Now, broadcaster, CancellationToken.None);

        Assert.Equal(1, closed);
        var gone = Read(ctx, "gone");
        Assert.Equal(expiry, gone.EndedAt);
        Assert.Null(gone.EndedById);
        Assert.Null(Read(ctx, "running").EndedAt);
        Assert.Equal(["gone"], reports);
    }

    [Fact]
    public async Task Sweep_AlreadyEndedByHand_KeepsEnder()
    {
        using var ctx = new SqliteTestContext();
        var endedAt = Now.AddMinutes(-20);
        Store(ctx, Alarm("hand", Now.AddMinutes(-1), a =>
        {
            a.EndedAt = endedAt;
            a.EndedById = "boss";
        }));
        var (broadcaster, reports) = Broadcast();

        await using var db = ctx.NewContext();
        var closed = await EmergencyAlarmExpiryWorker.SweepAsync(db, Now, broadcaster, CancellationToken.None);

        Assert.Equal(0, closed);
        var alarm = Read(ctx, "hand");
        Assert.Equal(endedAt, alarm.EndedAt);
        Assert.Equal("boss", alarm.EndedById);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Sweep_ExactlyAtExpiry_Closes()
    {
        using var ctx = new SqliteTestContext();
        Store(ctx, Alarm("edge", Now));
        var (broadcaster, reports) = Broadcast();

        await using var db = ctx.NewContext();
        var closed = await EmergencyAlarmExpiryWorker.SweepAsync(db, Now, broadcaster, CancellationToken.None);

        Assert.Equal(1, closed);
        Assert.Equal(Now, Read(ctx, "edge").EndedAt);
        Assert.Equal(["edge"], reports);
    }

    [Fact]
    public async Task Sweep_RunTwice_ClosesAndReportsOnce()
    {
        using var ctx = new SqliteTestContext();
        Store(ctx, Alarm("gone", Now.AddMinutes(-5)));
        var (broadcaster, reports) = Broadcast();

        await using var db = ctx.NewContext();
        var first = await EmergencyAlarmExpiryWorker.SweepAsync(db, Now, broadcaster, CancellationToken.None);
        var second = await EmergencyAlarmExpiryWorker.SweepAsync(db, Now, broadcaster, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(["gone"], reports);
    }

    [Fact]
    public async Task Sweep_EndedByHandBetweenSelectAndClaim_KeepsEnderAndStaysSilent()
    {
        using var ctx = new SqliteTestContext();
        Store(ctx, Alarm("race", Now.AddMinutes(-1)));
        var (broadcaster, reports) = Broadcast();
        var endedAt = Now.AddSeconds(-30);
        var interceptor = new BeforeFirstUpdate(() =>
        {
            using var other = ctx.NewContext();
            other.EmergencyAlarms.Where(a => a.Id == "race")
                .ExecuteUpdate(s => s.SetProperty(a => a.EndedAt, endedAt).SetProperty(a => a.EndedById, "boss"));
        });

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(interceptor)
            .Options);
        var closed = await EmergencyAlarmExpiryWorker.SweepAsync(db, Now, broadcaster, CancellationToken.None);

        Assert.Equal(0, closed);
        var alarm = Read(ctx, "race");
        Assert.Equal(endedAt, alarm.EndedAt);
        Assert.Equal("boss", alarm.EndedById);
        Assert.Empty(reports);
    }

    // one concurrent write before the first bulk update
    private sealed class BeforeFirstUpdate(Action concurrent) : DbCommandInterceptor
    {
        private Action? _pending = concurrent;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _pending, null) is { } run)
            {
                run();
            }
            return ValueTask.FromResult(result);
        }
    }
}
