using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.People;

/// <summary>Form model for creating/editing a person; profile multi-fields use stable per-item objects for inline editing.</summary>
public class PersonInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public LifeStatus LifeStatus { get; set; } = LifeStatus.Alive;
    public Classification Classification { get; set; } = Classification.Unknown;
    public string? ClassificationJustification { get; set; }
    public DocumentClassification SecrecyLevel { get; set; } = DocumentClassification.None;

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }

    public List<AliasInput> Aliases { get; set; } = new();
    public List<PhoneInput> PhoneNumbers { get; set; } = new();
    public List<VehicleInput> Vehicles { get; set; } = new();
    public List<LocationInput> Locations { get; set; } = new();
    public List<WeaponInput> Weapons { get; set; } = new();
}

/// <summary>Uniform view over a profile multi-field so the generic chip input can target differently-named fields; implemented explicitly to keep the original fields intact.</summary>
public interface IProfileMultiple
{
    string MainValue { get; set; }
    string? Extra { get; set; }
}

public class AliasInput
{
    public string AliasName { get; set; } = string.Empty;
}

public class PhoneInput
{
    public string Number { get; set; } = string.Empty;
    public string? Designation { get; set; }
}

public class VehicleInput : IProfileMultiple
{
    public string Designation { get; set; } = string.Empty;
    public string? LicensePlate { get; set; }

    string IProfileMultiple.MainValue { get => Designation; set => Designation = value; }
    string? IProfileMultiple.Extra { get => LicensePlate; set => LicensePlate = value; }
}

public class LocationInput : IProfileMultiple
{
    public string Text { get; set; } = string.Empty;
    public string? Note { get; set; }

    string IProfileMultiple.MainValue { get => Text; set => Text = value; }
    string? IProfileMultiple.Extra { get => Note; set => Note = value; }
}

public class WeaponInput : IProfileMultiple
{
    public string Text { get; set; } = string.Empty;

    string IProfileMultiple.MainValue { get => Text; set => Text = value; }
    string? IProfileMultiple.Extra { get => null; set { } }
}
