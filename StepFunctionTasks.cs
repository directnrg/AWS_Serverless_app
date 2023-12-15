using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.Core;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Amazon.S3;
using Amazon.S3.Model;
using System.Text.Json;
using S3Object = Amazon.Rekognition.Model.S3Object;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]



namespace FabianSoto_Lab4;


public class StepFunctionTasks
{
    IAmazonS3 S3Client { get; set; }
    const string ThumbnailBucketName = "images-target-bucket-fabian-lab4";
    private readonly AmazonRekognitionClient rekognitionClient = new();
    private readonly DynamoDBContext dbContext = new (new AmazonDynamoDBClient());

    /// <summary>
    /// Default constructor that Lambda will invoke.
    /// </summary>
    public StepFunctionTasks()
    {
        S3Client = new AmazonS3Client();
    }
    /// <summary>
    /// Constructs an instance with a preconfigured S3 client. This can be used for testing the outside of the Lambda environment.
    /// </summary>
    /// <param name="s3Client"></param>
    public StepFunctionTasks(IAmazonS3 s3Client)
    {
        this.S3Client = s3Client;
    }

    public static ImageProcessingState VerifyFileContentType(Object eventObject, ILambdaContext context)
    {
        var state = new ImageProcessingState();

        context.Logger.LogLine("Checking if the file is an image.");

        try
        {
            // Parse the JSON string dynamically
            var jObject = JsonDocument.Parse(JsonSerializer.Serialize(eventObject)).RootElement;
            if (jObject.TryGetProperty("detail", out JsonElement detailElement))
            {
                var bucketName = detailElement.GetProperty("bucket").GetProperty("name").GetString();
                var objectKey = detailElement.GetProperty("object").GetProperty("key").GetString();

                if (string.IsNullOrEmpty(bucketName) || string.IsNullOrEmpty(objectKey))
                {
                    context.Logger.LogLine("EventBridge event does not contain valid S3 event data.");
                    return state;
                }
                state.BucketName = bucketName;
                state.ObjectKey = objectKey;

                state.IsImage = objectKey.ToLower().Contains(".jpg") || objectKey.ToLower().Contains(".jpeg") || objectKey.ToLower().Contains(".png");
            }
            else
            {
                context.Logger.LogLine("The 'detail' property is missing in the JSON object.");
            }

            context.Logger.LogLine($"Verification of Content Type completed. Is Image: {state.IsImage}");
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"Error processing JSON: {ex.Message}");
        }
        return state;
    }

    // Task for label detection
    public async Task<ImageProcessingState> DetectLabels(ImageProcessingState state, ILambdaContext context)
    {
        context.Logger.LogLine("Starting label detection.");
        if (state.IsImage)
        {
            var detectLabelsResponse = await rekognitionClient.DetectLabelsAsync(new DetectLabelsRequest
            {
                Image = new Amazon.Rekognition.Model.Image
                {
                    S3Object = new S3Object
                    {
                        Bucket = state.BucketName,
                        Name = state.ObjectKey
                    }
                },
                MinConfidence = 90F
            });

            state.Labels = detectLabelsResponse.Labels
                .Where(label => label.Confidence > 90)
                .ToDictionary(label => label.Name, label => label.Confidence);

            context.Logger.LogLine("Label detection completed.");
        }
        return state;
    }

    public async Task<ImageProcessingState> GenerateThumbnail(ImageProcessingState state, ILambdaContext context)
    {
        context.Logger.LogLine("Starting thumbnail generation.");

        if (state.IsImage)
        {
            try
            {
                using GetObjectResponse response = await S3Client.GetObjectAsync(state.BucketName, state.ObjectKey);
                using Stream responseStream = response.ResponseStream;

                using var memstream = new MemoryStream();
                var buffer = new byte[8192];
                var bytesRead = default(int);
                while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                    memstream.Write(buffer, 0, bytesRead);


                // Perform image manipulation
                using var transformedImageStream = GcImagingOperations.GetConvertedImage(memstream.ToArray());
                PutObjectRequest putRequest = new()
                {
                    BucketName = ThumbnailBucketName,
                    Key = $"thumbnail-{state.ObjectKey}",
                    ContentType = response.Headers.ContentType,
                    InputStream = transformedImageStream
                };
                await S3Client.PutObjectAsync(putRequest);
                state.ThumbnailKey = putRequest.Key; // To Update the state with the thumbnail key
            }
            catch (Exception e)
            {
                context.Logger.LogError($"Error processing thumbnail for {state.ObjectKey} in bucket {state.BucketName}.");
                context.Logger.LogError(e.Message);
                throw;
            }
        }

        return state;
    }
    public async Task SaveToDynamoDB(ImageProcessingState[] states, ILambdaContext context)
    {
        context.Logger.LogLine("Saving data to DynamoDB.");

        // Assuming states[0] is from GenerateThumbnail and states[1] is from DetectLabels
        var combinedState = new ImageProcessingState
        {
            BucketName = states[0].BucketName,
            ObjectKey = states[0].ObjectKey,
            IsImage = states[0].IsImage,
            ThumbnailKey = states[0].ThumbnailKey,
            Labels = states.Length > 1 ? states[1].Labels : null
        };

        var imageRecord = new ImageRecord
        {
            ImageURL = $"s3://{combinedState.BucketName}/{combinedState.ObjectKey}",
            Labels = combinedState.Labels,
            ThumbnailURL = $"s3://{ThumbnailBucketName}/{combinedState.ThumbnailKey}"
        };

        await dbContext.SaveAsync(imageRecord);
        context.Logger.LogLine("Data saved to DynamoDB.");
    }

}
