using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor.Grafana
{
    public class GrafanaExportService : IDisposable
    {
        private Timer? _timer;
        private static readonly ILogger Logger = LoggerFactory.Create();
        private readonly GoogleDriveService _googleDriveService;
        private IWebDriver _driver;

        public GrafanaExportService(GoogleDriveService googleDriveService, ApplicationConfiguration config)
        {
            Logger.Information("Initializing GrafanaExportService.");
            _googleDriveService = googleDriveService;
        }

        public async Task RunBackupProcess(CancellationToken cancellationToken)
        {
            Logger.Information("Starting Webistecs DB Backup Service...");

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

            var timeRange = ExtractTimeRangeFromUrl(grafanaUrl);

            var options = new ChromeOptions();
            options.AddArgument("--headless");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-setuid-sandbox");
            options.AddArgument("--force-device-scale-factor=2");

            _driver = new ChromeDriver("/usr/bin/chromedriver", options);

            try
            {
                _driver.Navigate().GoToUrl(grafanaUrl);
                await Task.Delay(5000);

                ((IJavaScriptExecutor)_driver).ExecuteScript("document.body.style.zoom='100%'");
                await Task.Delay(2000);

                var collapsedRows = _driver.FindElements(By.CssSelector(".dashboard-row--collapsed"));
                foreach (var row in collapsedRows)
                {
                    try
                    {
                        var toggleButton = row.FindElement(By.CssSelector("button"));
                        toggleButton.Click();
                    }
                    catch
                    {
                        Logger.Warning("⚠️ Failed to expand a row");
                    }
                }

                await AutoScroll();
                
                _driver.Manage().Window.Size = new System.Drawing.Size(1920, 3350);
                await Task.Delay(2000);
                await CaptureAndConvertScreenshot(timeRange, "4k");

                await AutoScroll();
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

        private async Task CaptureAndConvertScreenshot(string timeRange, string version)
        {
            var pdfOutput = $"/tmp/{timeRange}-{version}.pdf";
            try
            {
                var screenshotName = $"webistecs-{timeRange}-{version}.png";
                var screenshotPath = $"/tmp/{screenshotName}".Replace("//", "/");

                var screenshot = ((ITakesScreenshot)_driver).GetScreenshot();
                screenshot.SaveAsFile(screenshotPath);

                if (File.Exists(screenshotPath))
                {
                    Logger.Debug($"✅ {version} screenshot saved successfully: {screenshotPath}");
                }
                else
                {
                    Logger.Error($"❌ Failed to save {version} screenshot: {screenshotPath}");
                }

                ConvertImageToPdf(screenshotName, pdfOutput);

                if (File.Exists(pdfOutput))
                {
                    await _googleDriveService.UploadFileToCorrectFolder(pdfOutput,
                        WebistecsConstants.GrafanaBackupFolderId, "application/pdf");
                    Logger.Information($"✅ {version} PDF successfully uploaded: {Path.GetFileName(pdfOutput)}");
                }
                else
                {
                    Logger.Error($"❌ {version} PDF file does not exist after conversion.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"🔥 Error during {version} screenshot capture or upload: {ex.Message}");
                Logger.Error(ex.StackTrace);
            }
        }

        private async Task AutoScroll()
        {
            Logger.Information("Starting auto-scroll.");
            var jsExecutor = (IJavaScriptExecutor)_driver;
            var lastHeight = Convert.ToInt64(jsExecutor.ExecuteScript("return document.body.scrollHeight"));

            while (true)
            {
                jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(1000);
                var newHeight = Convert.ToInt64(jsExecutor.ExecuteScript("return document.body.scrollHeight;"));
                if (newHeight == lastHeight)
                    break;
                lastHeight = newHeight;
            }

            Logger.Information("Auto-scroll completed.");
        }


        private void ConvertImageToPdf(string imagePath, string outputPdfPath)
        {
            try
            {
                var correctedImagePath = $"/tmp/{imagePath}".Replace("//", "/");

                Logger.Information($"📄 Converting image to PDF: {correctedImagePath} → {outputPdfPath}");

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
                return "kubernetes";
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