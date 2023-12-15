
using Amazon.DynamoDBv2.DataModel;

namespace FabianSoto_Lab4
{
    [DynamoDBTable("ImageMetadata")]
    public class ImageRecord
    {
        [DynamoDBHashKey]
        public string? ImageURL { get; set; }

        [DynamoDBProperty]
        public Dictionary<string, float>? Labels { get; set; }

        [DynamoDBProperty]
        public string? ThumbnailURL { get; set; }
    }
}