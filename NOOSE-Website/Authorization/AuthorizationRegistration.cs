using Microsoft.AspNetCore.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Authorization;

/// <summary>Registers all authorization policies.</summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class AuthorizationRegistration
{
    public static IServiceCollection AddNooseAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, RankAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PartnerRankAuthorizationHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.ActiveAgent, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.GetStatus() == AgentStatus.Active))
            .AddPolicy(Policies.Leadership, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new RankRequirement(Rank.SpecialAgentInCharge)))
            .AddPolicy(Policies.Admin, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsAdmin()))
            .AddPolicy(Policies.BootstrapAdmin, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsBootstrapAdmin()))
            .AddPolicy(Policies.AiOwner, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsAiOwner()))
            // write guards
            .AddPolicy(Policies.WriteAccess, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayWrite()))
            .AddPolicy(Policies.DocumentAuthor, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayWrite() || ctx.User.IsPartner()))
            .AddPolicy(Policies.OnlyReadMode, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsOnlyReader()))
            // partner/internal split
            .AddPolicy(Policies.PartnerView, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsPartner()))
            .AddPolicy(Policies.InternalAgent, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => !ctx.User.IsPartner()))
            // page access
            .AddPolicy(Policies.LeadershipPage, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsLeadership() || ctx.User.IsOnlyReader()))
            .AddPolicy(Policies.HighestClassificationPage, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayHighestClassification() || ctx.User.IsOnlyReader()))
            .AddPolicy(Policies.AdminPage, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsAdmin() || ctx.User.IsOnlyReader()))
            .AddPolicy(Policies.CounterIntel, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayCounterIntel()))
            .AddPolicy(Policies.HighestClassification, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new RankRequirement(Rank.SeniorSpecialAgent)))
            .AddPolicy(Policies.PromotionDecide, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new RankRequirement(PromotionRules.DeciderFloor)))
            .AddPolicy(Policies.PromotionDecideWrite, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayWrite() && ctx.User.MayPromotionDecide()))
            .AddPolicy(Policies.DivisionSetWrite, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayWrite() && ctx.User.MayDivisionSet()))
            .AddPolicy(Policies.FirearmUseReport, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayReportFirearmUse()))
            // recruiting
            // MayApply, not the raw status: a partner applies while staying Active, so the status alone would
            // lock the very accounts this portal has to serve
            .AddPolicy(Policies.ApplicantPortal, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayApply()))
            .AddPolicy(Policies.HrbOrLeadership, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsHrbOrLeadership()))
            .AddPolicy(Policies.HrbOrLeadershipWrite, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayWrite() && ctx.User.IsHrbOrLeadership()))
            .AddPolicy(Policies.LeadershipWrite, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayWrite() && ctx.User.IsLeadership()))
            // public area
            .AddPolicy(Policies.CitizenPortal, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.MayUseCitizenPortal()));

        return services;
    }
}
