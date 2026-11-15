using Microsoft.Extensions.Options;

namespace NOOSE_Website.Infrastructure.Storage;

/// <inheritdoc cref="IInvestigationPhotoStorageService" />
public class InvestigationPhotoStorageService(IWebHostEnvironment env, IOptions<FileUploadOptions> options)
    : RecordPhotoStorageService(env, options.Value, options.Value.InvestigationsPath), IInvestigationPhotoStorageService;
