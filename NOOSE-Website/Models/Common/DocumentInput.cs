using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>Input model for a library document.</summary>
public class DocumentInput
{
    public string Title { get; set; } = string.Empty;
    public string? Category { get; set; }

    /// <summary>WYSIWYG HTML; sanitized server-side in the service.</summary>
    public string ContentHtml { get; set; } = string.Empty;

    /// <summary>The editor showed the text locked (an imported original): keep the stored text whatever
    /// <see cref="ContentHtml"/> carries, also when the document was converted in the meantime.</summary>
    public bool KeepContent { get; set; }

    /// <summary>Classification level; the service checks the actor may set it.</summary>
    public DocumentClassification Classification { get; set; }

    /// <summary>When set, the document is taskforce-internal (created from a taskforce's sources).</summary>
    public string? OwnerTaskforceId { get; set; }
}
