namespace FabianSoto_Lab4;

/// <summary>
/// The state passed between the step function executions.
/// </summary>
public class ImageProcessingState
{
    public string? BucketName { get; set; }
    public string? ObjectKey { get; set; }
    public bool IsImage { get; set; }
    public Dictionary<string, float>? Labels { get; set; }
    public string? ThumbnailKey { get; set; }
}