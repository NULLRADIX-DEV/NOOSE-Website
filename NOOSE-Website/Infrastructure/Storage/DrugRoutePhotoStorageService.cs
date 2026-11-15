using Microsoft.Extensions.Options;

namespace NOOSE_Website.Infrastructure.Storage;

/// <inheritdoc cref="IDrugRoutePhotoStorageService" />
public class DrugRoutePhotoStorageService(IWebHostEnvironment env, IOptions<FileUploadOptions> options)
    : RecordPhotoStorageService(env, options.Value, options.Value.DrugRoutesPath), IDrugRoutePhotoStorageService;
