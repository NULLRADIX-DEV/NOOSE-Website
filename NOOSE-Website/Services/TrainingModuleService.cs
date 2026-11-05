using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ITrainingModuleService" />
public class TrainingModuleService(IDbContextFactory<AppDbContext> dbFactory) : ITrainingModuleService
{
    public async Task<List<TrainingModule>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TrainingModules
            .OrderBy(m => m.Sorting).ThenBy(m => m.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<TrainingModule>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TrainingModules
            .Where(m => m.IsActive)
            .OrderBy(m => m.Sorting).ThenBy(m => m.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<TrainingModule> CreateAsync(ModuleInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        var name = (input.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Der Modul-Name darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.TrainingModules.AnyAsync(m => m.Name == name, cancellationToken))
        {
            throw new InvalidOperationException($"Ein Modul „{name}“ existiert bereits.");
        }

        var module = new TrainingModule
        {
            Name = name,
            Description = Empty(input.Description),
            IsActive = input.IsActive,
            IsMandatory = input.IsMandatory,
            Sorting = input.Sorting,
        };
        db.TrainingModules.Add(module);
        await db.SaveChangesAsync(cancellationToken);
        return module;
    }

    public async Task UpdateAsync(string moduleId, ModuleInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        var name = (input.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Der Modul-Name darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var module = await db.TrainingModules.FirstOrDefaultAsync(m => m.Id == moduleId, cancellationToken)
            ?? throw new InvalidOperationException("Modul nicht gefunden.");
        if (await db.TrainingModules.AnyAsync(m => m.Id != moduleId && m.Name == name, cancellationToken))
        {
            throw new InvalidOperationException($"Ein Modul „{name}“ existiert bereits.");
        }

        module.Name = name;
        module.Description = Empty(input.Description);
        module.IsActive = input.IsActive;
        module.IsMandatory = input.IsMandatory;
        module.Sorting = input.Sorting;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string moduleId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var module = await db.TrainingModules.FirstOrDefaultAsync(m => m.Id == moduleId, cancellationToken);
        if (module is null)
        {
            return;
        }
        // remove completions too, avoid orphans
        var completions = await db.AgentModuleCompletions
            .Where(c => c.ModuleId == moduleId)
            .ToListAsync(cancellationToken);
        db.AgentModuleCompletions.RemoveRange(completions);
        db.TrainingModules.Remove(module);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AgentModuleStatus>> GetStatusForAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var completions = await db.AgentModuleCompletions
            .Include(c => c.Module)
            .Where(c => c.AgentId == agentId)
            .ToListAsync(cancellationToken);
        var completionByModule = completions
            .Where(c => c.Module is not null)
            .ToDictionary(c => c.ModuleId, c => c);

        var activeModules = await db.TrainingModules
            .Where(m => m.IsActive)
            .OrderBy(m => m.Sorting).ThenBy(m => m.Name)
            .ToListAsync(cancellationToken);

        var result = new List<AgentModuleStatus>();
        var seen = new HashSet<string>();
        foreach (var module in activeModules)
        {
            seen.Add(module.Id);
            completionByModule.TryGetValue(module.Id, out var done);
            result.Add(new AgentModuleStatus(module, done?.Id, done?.CompletedAt, done?.CompleterName, done?.Note));
        }
        // keep already-completed but now-inactive modules visible as history
        foreach (var done in completions.Where(c => c.Module is not null && !seen.Contains(c.ModuleId)))
        {
            result.Add(new AgentModuleStatus(done.Module!, done.Id, done.CompletedAt, done.CompleterName, done.Note));
        }
        return result
            .OrderBy(s => s.Module.Sorting).ThenBy(s => s.Module.Name)
            .ToList();
    }

    public async Task<AgentModuleCompletion> MarkCompletedAsync(string agentId, string moduleId, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // ticking off is HRB work, unlike creating or deleting a module, which stays with leadership
        Permission.RequireHrbOrLeadershipWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the training catalogue is NOOSE-internal; partners carry a PartnerRank, not a NOOSE Rank
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Module können nur für aktive FIB-Agents markiert werden.");
        }
        if (!await db.TrainingModules.AnyAsync(m => m.Id == moduleId, cancellationToken))
        {
            throw new InvalidOperationException("Das gewählte Modul wurde nicht gefunden.");
        }
        if (await db.AgentModuleCompletions.AnyAsync(c => c.AgentId == agentId && c.ModuleId == moduleId, cancellationToken))
        {
            throw new InvalidOperationException("Dieses Modul ist für den Agenten bereits als abgeschlossen markiert.");
        }

        var completion = new AgentModuleCompletion
        {
            AgentId = agentId,
            ModuleId = moduleId,
            CompletedAt = DateTime.UtcNow,
            CompleterName = actor.GetCodename(),
            Note = Empty(note),
        };
        db.AgentModuleCompletions.Add(completion);
        await db.SaveChangesAsync(cancellationToken);
        return completion;
    }

    public async Task UnmarkCompletedAsync(string completionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var completion = await db.AgentModuleCompletions.FirstOrDefaultAsync(c => c.Id == completionId, cancellationToken);
        if (completion is null)
        {
            return;
        }
        db.AgentModuleCompletions.Remove(completion); // soft delete via interceptor
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
