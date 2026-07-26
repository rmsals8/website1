namespace backend.Models.Dtos;

public record PageDto(
    string Id,
    int DisplayIndex,
    string SourceType,
    int Rotation,
    double CropX,
    double CropY,
    double CropWidth,
    double CropHeight,
    double Brightness,
    double Contrast,
    double Midtones,
    bool AutoExposure,
    double PageWidthPt,
    double PageHeightPt
);

public record SessionDto(string SessionId, IReadOnlyList<PageDto> Pages, int MaxPages);

public record UpdatePageRequest(
    int? Rotation,
    int? RotateBy,
    double? CropX,
    double? CropY,
    double? CropWidth,
    double? CropHeight,
    double? Brightness,
    double? Contrast,
    double? Midtones,
    bool? AutoExposure
);

public record ReorderRequest(List<string> OrderedPageIds);

public record ErrorResponse(string Message);
