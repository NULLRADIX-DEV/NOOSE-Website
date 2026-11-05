namespace NOOSE_Website.Models.Enums;

/// <summary>FIB service ranks; persisted as int, so a value never moves.</summary>
public enum Rank
{
    Trainee = 1,
    SpecialAgent = 2,
    SeniorSpecialAgent = 3,
    SpecialAgentInCharge = 4,
    SectionChief = 5, // leadership starts here
    AssistantDirector = 6, // Board starts here
    DeputyDirector = 7,
    Director = 8,
}
