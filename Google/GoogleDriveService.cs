using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Newtonsoft.Json;
using Serilog;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Domain;
using Webistecs_Monitor.Logging;
using static Webistecs_Monitor.WebistecsConstants;

namespace Webistecs_Monitor.Google
{
    public class GoogleDriveService
    {
        private readonly ApplicationConfiguration _config;
        private readonly DriveService? _driveService;
        private static readonly ILogger Logger = LoggerFactory.Create();
        private const string ConfigFolderId = "1nUmMGHn85aBRFtv7pRTqnQseSAxplJgq"; // Used for scheduled_tasks_status.json

        public GoogleDriveService(ApplicationConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config), "❌ Config is null!");
            _driveService = InitializeDriveService();

            if (_driveService == null)
            {
                Logger.Error("❌ Google Drive Service failed to initialize! Drive operations will not work.");
            }
        }

        private DriveService? InitializeDriveService()
        {
            try
            {
                var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
                if (string.IsNullOrEmpty(credentialsPath))
                {
                    Logger.Error("❌ GOOGLE_APPLICATION_CREDENTIALS is not set or empty!");
                    return null;
                }

                Logger.Information("🔑 Using Google credentials from: {Path}", credentialsPath);

                using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
                var credential = GoogleCredential.FromStream(stream)
                    .CreateScoped(DriveService.ScopeConstants.DriveFile);

                Logger.Information("✅ Google Drive authentication successful.");

                return new DriveService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = ApplicationName,
                });
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error constructing Google Drive Service: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Uploads or updates a file to Google Drive.
        /// This method is used for logs, backups, and Grafana dashboards.
        /// </summary>
        public async Task UploadFileToGoogleDrive(string filePath, string parentFolderId, string fileType,
            string contentType = "application/octet-stream")
        {
            if (!File.Exists(filePath))
            {
                Logger.Error("❌ File does not exist: {FilePath}", filePath);
                return;
            }

            var fileName = Path.GetFileName(filePath);
            var existingFileId = await FindFileInGoogleDrive(fileName, parentFolderId);

            try
            {
                if (!string.IsNullOrEmpty(existingFileId))
                {
                    Logger.Information("📄 File {FileName} already exists. Updating...", fileName);
                    await using var fileStream = new FileStream(filePath, FileMode.Open);
                    var updateRequest = _driveService.Files.Update(
                        new global::Google.Apis.Drive.v3.Data.File(),
                        existingFileId,
                        fileStream,
                        contentType
                    );
                    await updateRequest.UploadAsync();
                    Logger.Information("✅ Successfully updated: {FileName}", fileName);
                }
                else
                {
                    Logger.Information("📂 Uploading new file: {FileName}", fileName);

                    var fileMetadata = new global::Google.Apis.Drive.v3.Data.File
                    {
                        Name = fileName,
                        Parents = new List<string> { parentFolderId }
                    };

                    await using var fileStream = new FileStream(filePath, FileMode.Open);
                    var uploadRequest = _driveService.Files.Create(fileMetadata, fileStream, contentType);
                    uploadRequest.Fields = "id";
                    await uploadRequest.UploadAsync();

                    Logger.Information("✅ {FileType} uploaded to Google Drive: {FileName}", fileType, fileName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error uploading {FileType} to Google Drive: {Message}", fileType, ex.Message);
            }
        }

        /// <summary>
        /// Finds or creates a folder in Google Drive.
        /// </summary>
        private async Task<string> FindOrCreateFolderInGoogleDrive(string parentFolderId, string folderName)
        {
            var query =
                $"name = '{folderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false and '{parentFolderId}' in parents";
            var folder = await FindFileOrFolderAsync(query);

            if (folder != null)
            {
                Logger.Information("📁 Found existing folder: {FolderName}", folderName);
                return folder.Id;
            }

            Logger.Information("📁 Creating new folder: {FolderName}", folderName);
            return await CreateFolderAsync(folderName, parentFolderId);
        }

        public async Task<string?> FindFileInGoogleDrive(string fileName, string parentFolderId)
        {
            try
            {
                Logger.Information("🔍 Searching for '{FileName}' in Google Drive (Folder ID: {ParentFolderId})...", fileName, parentFolderId);

                var request = _driveService.Files.List();
                request.Q = $"name = '{fileName}' and trashed = false and '{parentFolderId}' in parents";
                request.Fields = "files(id, name, parents, mimeType)";
                var result = await request.ExecuteAsync();

                if (result.Files.Count == 0)
                {
                    Logger.Warning("⚠️ File '{FileName}' NOT found in Google Drive. Listing all files in {ParentFolderId}...", fileName, parentFolderId);
                    await ListFilesInFolder(parentFolderId);
                    return null;
                }

                var file = result.Files.First();
                Logger.Information("✅ Found '{FileName}' (ID: {FileId}, Parent: {Parents}, Type: {MimeType})",
                    file.Name, file.Id, string.Join(",", file.Parents ?? new List<string>()), file.MimeType);

                return file.Id;
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error searching for '{FileName}' in Google Drive: {Message}", fileName, ex.Message);
                return null;
            }
        }

        private async Task<global::Google.Apis.Drive.v3.Data.File?> FindFileOrFolderAsync(string query)
        {
            var request = _driveService.Files.List();
            request.Q = query;
            request.Fields = "files(id, name)";
            var result = await request.ExecuteAsync();
            return result.Files.FirstOrDefault();
        }

        private async Task<string> CreateFolderAsync(string folderName, string? parentFolderId = null)
        {
            var fileMetadata = new global::Google.Apis.Drive.v3.Data.File
            {
                Name = folderName,
                MimeType = "application/vnd.google-apps.folder",
                Parents = parentFolderId != null ? new List<string> { parentFolderId } : null
            };

            var createRequest = _driveService.Files.Create(fileMetadata);
            createRequest.Fields = "id";
            var newFolder = await createRequest.ExecuteAsync();
            return newFolder.Id;
        }

        /// <summary>
        /// Deletes a file from Google Drive by ID.
        /// </summary>
        public async Task DeleteFileFromGoogleDrive(string fileId)
        {
            try
            {
                await _driveService.Files.Delete(fileId).ExecuteAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error deleting file from Google Drive: {Message}", ex.Message);
            }
        }

        public async Task UploadFileToCorrectFolder(string filePath, string folderId, string contentType)
        {
            if (!File.Exists(filePath))
            {
                Logger.Error("❌ File does not exist: {FilePath}", filePath);
                return;
            }

            try
            {
                Logger.Information("📤 Uploading file: {FileName} to folder {FolderId}", Path.GetFileName(filePath), folderId);

                await UploadFileToGoogleDrive(filePath, folderId, "Backup File", contentType);

                Logger.Information("✅ Successfully uploaded: {FileName} to {FolderId}", Path.GetFileName(filePath), folderId);
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error uploading file: {Message}", ex.Message);
            }
        }

        public async Task UploadLogsToGoogleDrive(string logFilePath)
        {
            if (!File.Exists(logFilePath))
            {
                Logger.Error("❌ Log file does not exist: {LogFilePath}", logFilePath);
                return;
            }

            try
            {
                Logger.Information("📤 Uploading log file: {FileName} to logs folder {FolderId}",
                    Path.GetFileName(logFilePath), WebistecsConstants.LogsBackupFolderId);

                await UploadFileToGoogleDrive(logFilePath, WebistecsConstants.LogsBackupFolderId, "Log File", "text/plain");

                Logger.Information("✅ Log file successfully uploaded: {FileName}", Path.GetFileName(logFilePath));
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error uploading log file to Google Drive: {Message}", ex.Message);
            }
        }

        public async Task<Dictionary<string, TaskMetadata>> ReadBackupStatusFromGoogleDrive()
        {
            try
            {
                Logger.Information("📥 Fetching `scheduled_tasks_status.json` from Google Drive (Config Folder ID: {ConfigFolderId})...", ConfigFolderId);

                var fileId = await FindFileInGoogleDrive("scheduled_tasks_status.json", ConfigFolderId);

                if (string.IsNullOrEmpty(fileId))
                {
                    Logger.Warning("⚠️ `scheduled_tasks_status.json` NOT FOUND in Google Drive config folder.");
                    return new Dictionary<string, TaskMetadata>(); // Return an empty schedule
                }

                using var stream = new MemoryStream();
                await _driveService.Files.Get(fileId).DownloadAsync(stream);
                stream.Position = 0;

                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();

                Logger.Information("✅ Successfully retrieved `scheduled_tasks_status.json` from Config Folder.");
                return JsonConvert.DeserializeObject<Dictionary<string, TaskMetadata>>(json)
                    ?? new Dictionary<string, TaskMetadata>();
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error reading `scheduled_tasks_status.json` from Google Drive: {Message}", ex.Message);
                return new Dictionary<string, TaskMetadata>();
            }
        }

        /// <summary>
        /// Lists all files in a given Google Drive folder.
        /// </summary>
        public async Task<List<global::Google.Apis.Drive.v3.Data.File>> ListFilesInFolder(string parentFolderId)
        {
            try
            {
                Logger.Information("📂 Listing files in Google Drive folder ID: {ParentFolderId}...", parentFolderId);

                var request = _driveService.Files.List();
                request.Q = $"'{parentFolderId}' in parents and trashed = false";
                request.Fields = "files(id, name, mimeType, createdTime)";

                var result = await request.ExecuteAsync();
                var files = result.Files?.ToList() ?? new List<global::Google.Apis.Drive.v3.Data.File>();

                if (files.Count == 0)
                {
                    Logger.Warning("⚠️ No files found in folder {ParentFolderId}", parentFolderId);
                }
                else
                {
                    Logger.Information("📂 Found {Count} file(s) in folder {ParentFolderId}:", files.Count, parentFolderId);
                    foreach (var file in files)
                    {
                        Logger.Information("   - 📄 File: {Name}, ID: {Id}, Created: {CreatedTime}, Type: {MimeType}",
                            file.Name, file.Id, file.CreatedTime, file.MimeType);
                    }
                }

                return files;
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error listing files in Google Drive folder {ParentFolderId}: {Message}", parentFolderId, ex.Message);
                return new List<global::Google.Apis.Drive.v3.Data.File>();
            }
        }

        public async Task SaveBackupStatusToGoogleDrive(Dictionary<string, TaskMetadata> backupStatus)
        {
            try
            {
                Logger.Information("📤 Updating `scheduled_tasks_status.json` in Google Drive Config Folder...");

                var fileId = await FindFileInGoogleDrive("scheduled_tasks_status.json", ConfigFolderId);
                var json = JsonConvert.SerializeObject(backupStatus, Formatting.Indented);
                var byteArray = Encoding.UTF8.GetBytes(json);

                using var stream = new MemoryStream(byteArray);

                if (!string.IsNullOrEmpty(fileId))
                {
                    var updateRequest = _driveService.Files.Update(
                        new global::Google.Apis.Drive.v3.Data.File(),
                        fileId,
                        stream,
                        "application/json"
                    );
                    await updateRequest.UploadAsync();
                    Logger.Information("✅ Successfully updated `scheduled_tasks_status.json` in Config Folder.");
                }
                else
                {
                    var fileMetadata = new global::Google.Apis.Drive.v3.Data.File
                    {
                        Name = "scheduled_tasks_status.json",
                        Parents = new List<string> { ConfigFolderId },
                        MimeType = "application/json"
                    };

                    var createRequest = _driveService.Files.Create(fileMetadata, stream, "application/json");
                    await createRequest.UploadAsync();

                    Logger.Information("✅ Successfully created new `scheduled_tasks_status.json` in Config Folder.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error saving `scheduled_tasks_status.json` to Google Drive: {Message}", ex.Message);
            }
        }
    }
}
