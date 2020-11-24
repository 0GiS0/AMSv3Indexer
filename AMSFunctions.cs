using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Extensions.Options;
using AMSv3Indexer.Models;
using Microsoft.Azure.Management.Media;
using System.Net.Http;
using System.Linq;
using Microsoft.Azure.Management.Media.Models;
using System;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AMSv3Indexer
{
    public class AMSFunctions
    {
        private const string VideoAnalyzerTransformName = "VideoInsightsOnly";
        private const string OutputFolderName = @"Output";

        private AMSSettings _settings;
        private IAzureMediaServicesClient _client;
        private ILogger _log;

        public AMSFunctions(IOptions<AMSSettings> settings, IAzureMediaServicesClient mediaClient)
        {
            _settings = settings.Value;
            _client = mediaClient;
        }

        [FunctionName(nameof(IsAdult))]
        public async Task<HttpResponseMessage> IsAdult(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestMessage req,
            ILogger log)
        {
            _log = log;

            //1. Get the file from the HTTP Request body
            var provider = new MultipartMemoryStreamProvider();
            await req.Content.ReadAsMultipartAsync(provider);
            var file = provider.Contents.First();
            var fileInfo = file.Headers.ContentDisposition;
            var fileData = await file.ReadAsStreamAsync();

            var fileName = fileInfo.FileName.Replace(@"""", "");

            //2. Get or create the transform for Video Analysis
            await GetOrCreateTransformAsync();

            //3. Create an asset in the Azure Media Service Account
            var inputAsset = await CreateInputAssetAsync($"{Path.GetFileNameWithoutExtension(fileName)}-input", fileData);

            //4. Create an output asset in th Azure Media Service Account
            var outputAsset = await CreateOutputAssetAsync($"{Path.GetFileNameWithoutExtension(fileName)}-output");

            //5. Submit the job to initialize the process
            var jobName = $"job-{fileName}-{DateTime.Now.ToShortTimeString()}";
            await SubmitJobAsync(jobName, new JobInputAsset(inputAsset.Name), outputAsset.Name);

            //6. Wait for the job to finish
            var job = await WaitForJobToFinishAsync(jobName);

            _log.LogInformation($"Job finished with state {job.State}");

            //7. Download json files from the output
            await DownloadOutputAssetAsync(outputAsset.Name);

            //8. Check if it's adult or not
            var isAdult = false;
            if (job.State == JobState.Finished)
            {
                isAdult = IsAdultCheck(outputAsset.Name);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(isAdult.ToString().ToLower()) };
        }

        /// <summary>
        /// Download the assets into the Ouput folder
        /// </summary>
        /// <param name="outputAssetName"></param>
        /// <returns></returns>
        private async Task DownloadOutputAssetAsync(string outputAssetName)
        {
            if (!Directory.Exists(OutputFolderName))
            {
                Directory.CreateDirectory(OutputFolderName);
            }

            // Use Media Service and Storage APIs to download the output files to a local folder
            AssetContainerSas assetContainerSas = _client.Assets.ListContainerSas(
                            _settings.ResourceGroup,
                            _settings.AccountName,
                            outputAssetName,
                            permissions: AssetContainerPermission.Read,
                            expiryTime: DateTime.UtcNow.AddHours(1).ToUniversalTime()
                            );

            Uri containerSasUrl = new Uri(assetContainerSas.AssetContainerSasUrls.FirstOrDefault());
            BlobContainerClient container = new BlobContainerClient(containerSasUrl);

            string directory = Path.Combine(OutputFolderName, outputAssetName);
            Directory.CreateDirectory(directory);

            _log.LogInformation("Downloading results to {0}.", directory);

            string continuationToken = null;

            do
            {
                var resultSegment = container.GetBlobs().AsPages(continuationToken);

                foreach (Azure.Page<BlobItem> blobPage in resultSegment)
                {
                    foreach (BlobItem blobItem in blobPage.Values)
                    {

                        var blobClient = container.GetBlobClient(blobItem.Name);
                        string filename = Path.Combine(directory, blobItem.Name);
                        await blobClient.DownloadToAsync(filename);
                    }

                    // Get the continuation token and loop until it is empty.
                    continuationToken = blobPage.ContinuationToken;
                }

            } while (continuationToken != "");

            _log.LogInformation("Download complete.");
        }

        /// <summary>
        /// Gets or create the tranform
        /// </summary>
        /// <returns></returns>
        private async Task<Transform> GetOrCreateTransformAsync()
        {
            var transform = await _client.Transforms.GetAsync(_settings.ResourceGroup, _settings.AccountName, VideoAnalyzerTransformName);

            if (transform == null) //the transform doesn't exist yet
            {
                //You need to specify what you want it produce as an output
                var output = new TransformOutput[]
                {
                    new TransformOutput
                    {
                           Preset = new VideoAnalyzerPreset(insightsToExtract: InsightsType.VideoInsightsOnly),
                           RelativePriority = Priority.High,

                    }
                };

                transform = await _client.Transforms.CreateOrUpdateAsync(_settings.ResourceGroup, _settings.AccountName, VideoAnalyzerTransformName, output);
            }

            return transform;
        }

        /// <summary>
        /// Create an asset in the Azure Media Service Account
        /// </summary>
        /// <param name="name"></param>
        /// <param name="fileData"></param>
        /// <returns></returns>
        private async Task<Asset> CreateInputAssetAsync(string name, Stream fileData)
        {
            //Check if the asset exists
            var inputAsset = await _client.Assets.GetAsync(_settings.ResourceGroup, _settings.AccountName, name);
            var assetName = name;

            if (inputAsset != null)
            {
                // Name collision! In order to get the sample to work, let's just go ahead and create a unique asset name
                // Note that the returned Asset can have a different name than the one specified as an input parameter.
                // You may want to update this part to throw an Exception instead, and handle name collisions differently.
                string uniqueness = $"-{Guid.NewGuid():N}";
                assetName += uniqueness;

                _log.LogInformation("Warning – found an existing Asset with name = " + name);
                _log.LogInformation("Creating an Asset with this name instead: " + assetName);
            }

            //Copy the file inside of the asset
            // Call Media Services API to create an Asset.
            // This method creates a container in storage for the Asset.
            // The files (blobs) associated with the asset will be stored in this container.
            var newAsset = await _client.Assets.CreateOrUpdateAsync(_settings.ResourceGroup, _settings.AccountName, assetName, new Asset());

            // Use Media Services API to get back a response that contains
            // SAS URL for the Asset container into which to upload blobs.
            // That is where you would specify read-write permissions 
            // and the exparation time for the SAS URL.
            var response = await _client.Assets.ListContainerSasAsync(
                _settings.ResourceGroup,
                _settings.AccountName,
                assetName,
                permissions: AssetContainerPermission.ReadWrite,
                expiryTime: DateTime.UtcNow.AddHours(4).ToUniversalTime());

            var sasUri = new Uri(response.AssetContainerSasUrls.First());

            var container = new BlobContainerClient(sasUri);
            var blob = container.GetBlobClient(name);

            await blob.UploadAsync(fileData);

            return newAsset;
        }

        /// <summary>
        /// Create an output asset in th Azure Media Service Account
        /// </summary>
        /// <param name="outputName"></param>
        /// <returns></returns>
        private async Task<Asset> CreateOutputAssetAsync(string outputName)
        {
            //Check if an Asset already exists
            var outputAsset = await _client.Assets.GetAsync(_settings.ResourceGroup, _settings.AccountName, outputName);
            string outputAssetName = outputName;

            if (outputAsset != null)
            {
                // Name collision! In order to get the sample to work, let's just go ahead and create a unique asset name
                // Note that the returned Asset can have a different name than the one specified as an input parameter.
                // You may want to update this part to throw an Exception instead, and handle name collisions differently.
                string uniqueness = $"-{Guid.NewGuid():N}";
                outputAssetName += uniqueness;

                _log.LogInformation("Warning – found an existing Asset with name = " + outputName);
                _log.LogInformation("Creating an Asset with this name instead: " + outputAssetName);
            }

            return await _client.Assets.CreateOrUpdateAsync(_settings.ResourceGroup, _settings.AccountName, outputAssetName, new Asset());
        }

        /// <summary>
        /// Submit the job to initialize the process
        /// </summary>
        /// <param name="jobName"></param>
        /// <param name="jobInput"></param>
        /// <param name="outputAssetName"></param>
        /// <returns></returns>
        private async Task<Job> SubmitJobAsync(string jobName, JobInputAsset jobInput, string outputAssetName)
        {
            JobOutput[] jobOutputs = { new JobOutputAsset(outputAssetName) };

            var job = await _client.Jobs.CreateAsync(_settings.ResourceGroup, _settings.AccountName, VideoAnalyzerTransformName, jobName, new Job { Input = jobInput, Outputs = jobOutputs });

            return job;
        }

        /// <summary>
        /// Wait for job to finish
        /// </summary>
        /// <param name="jobName"></param>
        /// <returns></returns>
        private async Task<Job> WaitForJobToFinishAsync(string jobName)
        {
            const int SleepIntervalMs = 2000;
            Job job;

            do
            {
                job = await _client.Jobs.GetAsync(_settings.ResourceGroup, _settings.AccountName, VideoAnalyzerTransformName, jobName);
                _log.LogInformation($"Job is '{job.State}'.");

                for (int i = 0; i < job.Outputs.Count; i++)
                {
                    JobOutput output = job.Outputs[i];
                    _log.LogInformation($"\tJobOutput[{i}] is '{output.State}'.");
                    if (output.State == JobState.Processing)
                    {
                        _log.LogInformation($"  Progress: '{output.Progress}'.");
                    }

                    _log.LogInformation(Environment.NewLine);
                }

                if (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled)
                {
                    await Task.Delay(SleepIntervalMs);
                }


            } while (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled);

            return job;
        }

        /// <summary>
        /// It checks if the content is adult only
        /// </summary>
        /// <param name="outputAssetName"></param>
        /// <returns></returns>
        private bool IsAdultCheck(string outputAssetName)
        {
            var isAdult = false;
            //read the insighs.json
            string insightsJson = File.ReadAllText($@"{OutputFolderName}\{outputAssetName}\insights.json");

            dynamic data = JsonConvert.DeserializeObject(insightsJson);

            if (data.visualContentModeration != null)
            {
                foreach (var item in data.visualContentModeration)
                {
                    _log.LogInformation($"content {item.adultScore}");
                    if (item.adultScore > 0.5)
                    {
                        isAdult = true;
                        break;
                    }
                }
            }

            _log.LogInformation($"Is Adult content? {isAdult}");

            return isAdult;
        }

    }
}
