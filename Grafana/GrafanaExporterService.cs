using Microsoft.Extensions.Hosting;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using ILogger = Serilog.ILogger;

/*/ Pretty much almost there just need to ensure that the correct board is made for 1 week and kubernetes
 */
namespace Webistecs_Monitor.Grafana
{
    public class GrafanaExportService
    {
        private Timer? _timer;
        private static readonly ILogger Logger = LoggerFactory.Create();
        private readonly GoogleDriveService _googleDriveService;
        private readonly ApplicationConfiguration _config;
        private IWebDriver _driver;

        public GrafanaExportService(GoogleDriveService googleDriveService, ApplicationConfiguration config)
        {
            Logger.Debug("Initializing GrafanaExportService. [to-delete]");
            _googleDriveService = googleDriveService;
            _config = config;
        }

        public async Task RunBackupProcess(CancellationToken cancellationToken)
        {
            Logger.Information("Starting Webistecs DB Backup Service...");
            Logger.Debug("Defining Grafana URLs for different time ranges. [to-delete]");

            var grafanaUrls = new[]
            {
                "http://192.168.68.107:30091/d/bNn5LUtiz/webistecs?orgId=1&refresh=1m&from=now-1h&to=now",
                "http://192.168.68.107:30091/d/bNn5LUtiz/webistecs?orgId=1&refresh=1m&from=now-24h&to=now",
                "http://192.168.68.107:30091/d/bNn5LUtiz/webistecs?orgId=1&refresh=1m&from=now-7d&to=now",
                "http://192.168.68.107:30091/d/k3_rook_global/kubernetes-overview?orgId=1&refresh=30s&from=now-7d&to=now"
            };

            foreach (var url in grafanaUrls)
            {
                Logger.Information($"Processing URL: {url}");
                await CaptureDashboardScreenshot(url);
            }

            Logger.Information("Webistecs DB process completed, stopping service.");
        }


        public async Task CaptureDashboardScreenshot(string grafanaUrl)
        {
            Logger.Information("🚀 Starting Grafana dashboard capture...");
            Logger.Debug($"🔗 Grafana URL: {grafanaUrl} [to-delete]");

            string timeRange = ExtractTimeRangeFromUrl(grafanaUrl);
            Logger.Debug($"🕒 Extracted time range: {timeRange} [to-delete]");

            var options = new ChromeOptions();
            options.AddArgument("--headless");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-setuid-sandbox");
            options.AddArgument("--force-device-scale-factor=1");

            Logger.Debug("🖥 Initializing ChromeDriver. [to-delete]");
            _driver = new ChromeDriver("/usr/bin/chromedriver", options);

            try
            {
                Logger.Debug("🌐 Navigating to Grafana URL. [to-delete]");
                _driver.Navigate().GoToUrl(grafanaUrl);
                await Task.Delay(5000);

                Logger.Debug("🔍 Setting browser zoom to 100%. [to-delete]");
                ((IJavaScriptExecutor)_driver).ExecuteScript("document.body.style.zoom='100%'");
                await Task.Delay(2000);

                Logger.Debug("📌 Checking and expanding collapsed dashboard rows. [to-delete]");
                var collapsedRows = _driver.FindElements(By.CssSelector(".dashboard-row--collapsed"));
                foreach (var row in collapsedRows)
                {
                    try
                    {
                        var toggleButton = row.FindElement(By.CssSelector("button"));
                        toggleButton.Click();
                        Logger.Debug("✅ Expanded a collapsed row. [to-delete]");
                    }
                    catch
                    {
                        Logger.Warning("⚠️ Failed to expand a row. [to-delete]");
                    }
                }

                Logger.Debug("📜 Performing initial auto-scroll to load all panels. [to-delete]");
                await AutoScroll();

                Logger.Debug("🖼 Setting viewport to 5700x3350 for full dashboard capture. [to-delete]");
                _driver.Manage().Window.Size = new System.Drawing.Size(5700, 3350);
                await Task.Delay(2000);

                Logger.Debug("📏 Adjusting viewport size based on content.");
                var jsExecutor = (IJavaScriptExecutor)_driver;
                long fullHeight = Convert.ToInt64(jsExecutor.ExecuteScript("return document.body.scrollHeight;"));
                long fullWidth = Convert.ToInt64(jsExecutor.ExecuteScript("return document.body.scrollWidth;"));

                fullWidth = Math.Max(fullWidth, 5700);
                fullHeight = Math.Min(fullHeight, 6000);

                _driver.Manage().Window.Size = new System.Drawing.Size((int)fullWidth, (int)fullHeight);
                Logger.Debug($"📏 Updated viewport size to {fullWidth}x{fullHeight}.");

                Logger.Debug("📜 Performing final auto-scroll to ensure full rendering.");
                await AutoScroll();

                // ✅ Capture Screenshot (Stored in /tmp/)
                Logger.Debug("📸 Capturing screenshot. [to-delete]");
                string screenshotName = $"webistecs-{timeRange}.png"; // Ensure correct naming
                await CaptureAndUploadScreenshot(screenshotName);

                // ✅ Convert PNG to PDF
                Logger.Debug("📄 Converting screenshot to PDF.");
                string pdfOutput = $"/tmp/webistecs-{timeRange}.pdf";
                ConvertImageToPdf(screenshotName, pdfOutput);

                // ✅ Upload only the PDF to Google Drive
                if (File.Exists(pdfOutput))
                {
                    Logger.Debug("📤 Uploading PDF to Google Drive...");
                    await _googleDriveService.UploadFileToCorrectFolder(pdfOutput, WebistecsConstants.GrafanaBackupFolderId,"application/pdf");
                    Logger.Information($"✅ PDF successfully uploaded: {Path.GetFileName(pdfOutput)}");
                }
                else
                {
                    Logger.Error("❌ PDF file does not exist after conversion.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"🔥 Error capturing Grafana dashboard: {ex.Message}");
                Logger.Error(ex.StackTrace);
            }
            finally
            {
                Logger.Debug("🛑 Quitting ChromeDriver.");
                _driver.Quit();
            }
        }


        private async Task AutoScroll()
        {
            Logger.Debug("Starting auto-scroll.");
            var jsExecutor = (IJavaScriptExecutor)_driver;
            var lastHeight = Convert.ToInt64(jsExecutor.ExecuteScript("return document.body.scrollHeight"));

            while (true)
            {
                jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(1000);
                var newHeight = Convert.ToInt64(jsExecutor.ExecuteScript("return document.body.scrollHeight"));
                if (newHeight == lastHeight)
                    break;
                lastHeight = newHeight;
            }

            Logger.Debug("Auto-scroll completed. [to-delete]");
        }

        private async Task CaptureAndUploadScreenshot(string screenshotName)
        {
            try
            {
                // ✅ Ensure the filename is correctly formatted
                string screenshotPath = $"/tmp/{screenshotName}".Replace("//", "/");

                // ✅ Ensure the /tmp/ directory exists
                string directoryPath = Path.GetDirectoryName(screenshotPath);
                if (!Directory.Exists(directoryPath))
                {
                    Logger.Warning($"📂 /tmp/ directory does not exist. Creating it now... [to-delete]");
                    Directory.CreateDirectory(directoryPath);
                }

                Logger.Debug($"📸 Capturing screenshot and saving to: {screenshotPath} [to-delete]");
                var screenshot = ((ITakesScreenshot)_driver).GetScreenshot();
                screenshot.SaveAsFile(screenshotPath);

                // ✅ Confirm the screenshot exists before proceeding
                if (File.Exists(screenshotPath))
                {
                    Logger.Information($"✅ Screenshot saved successfully: {screenshotPath}");
                }
                else
                {
                    Logger.Error($"❌ Failed to save screenshot: {screenshotPath}");
                    return; // Stop execution if screenshot doesn't exist
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"🔥 Error capturing screenshot: {ex.Message}");
                Logger.Error(ex.StackTrace);
            }
        }

        private void ConvertImageToPdf(string imagePath, string outputPdfPath)
        {
            try
            {
                // ✅ Ensure the image path is correctly set to /tmp/
                string correctedImagePath = $"/tmp/{imagePath}".Replace("//", "/");

                Logger.Debug($"📄 Converting image to PDF: {correctedImagePath} → {outputPdfPath} [to-delete]");

                // ✅ Check if the screenshot exists before attempting conversion
                if (!File.Exists(correctedImagePath))
                {
                    Logger.Error($"❌ Image file does not exist: {correctedImagePath}");
                    return;
                }

                using var image = XImage.FromFile(correctedImagePath);
                using var document = new PdfDocument();
                var page = document.AddPage();
                page.Width = image.PixelWidth;
                page.Height = image.PixelHeight;
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, 0, 0, page.Width, page.Height);
                document.Save(outputPdfPath);

                Logger.Information($"✅ PDF saved successfully: {outputPdfPath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"🔥 Error converting image to PDF: {ex.Message}");
            }
        }


        private string ExtractTimeRangeFromUrl(string url)
        {
            if (url.Contains("bNn5LUtiz/webistecs"))
            {
                if (url.Contains("from=now-1h")) return "webistecs-dashboard-1hour";
                if (url.Contains("from=now-24h")) return "webistecs-dashboard-24hours";
                if (url.Contains("from=now-7d")) return "webistecs-dashboard-1week";
            }
            else if (url.Contains("k3_rook_global/kubernetes-overview"))
            {
                return "kubernetes"; // Ensures it’s always named kubernetes.pdf
            }

            return "unknown";
        }


        public void Dispose()
        {
            _driver?.Quit();
            _timer?.Dispose();
        }
    }
}