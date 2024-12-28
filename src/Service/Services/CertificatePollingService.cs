using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shared.Models;
using Shared.Services;
using Service.Services;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace Service.Services
{
    // COM Interop declarations
    [ComImport]
    [Guid("37EABAF0-7FB6-11D0-8817-00A0C903B83C")]
    [ClassInterface(ClassInterfaceType.None)]
    public class CCertAdmin { }

    [ComImport]
    [Guid("C59B6972-755D-11D0-A3B9-00C04FB950DC")]
    [ClassInterface(ClassInterfaceType.None)]
    public class CCertView { }

    [ComImport]
    [Guid("C59B6973-755D-11D0-A3B9-00C04FB950DC")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ICertView
    {
        void OpenConnection([MarshalAs(UnmanagedType.BStr)] string config);
        void SetTable(int table);
        int GetColumnCount(int table);
        void SetResultColumnCount(int resultColumnCount);
        void SetResultColumn(int index);
        ICertViewRow OpenView();
    }

    [ComImport]
    [Guid("C59B6974-755D-11D0-A3B9-00C04FB950DC")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ICertViewRow
    {
        int Next();
        ICertViewColumn EnumCertViewColumn();
    }

    [ComImport]
    [Guid("C59B6975-755D-11D0-A3B9-00C04FB950DC")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ICertViewColumn
    {
        int Next();
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();
        object GetValue(int index);
    }

    [SupportedOSPlatform("windows")]
    public static class OidResolver
    {
        private const int CRYPT_OID_INFO_OID_KEY = 1;
        private const int CRYPT_OID_INFO_NAME_KEY = 2;
        private const int CRYPT_OID_INFO_ALGID_KEY = 3;
        private const int CRYPT_OID_INFO_SIGN_KEY = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_OID_INFO
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.LPStr)]
            public string pszOID;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszName;
            public int dwGroupId;
            public int dwValue;
            public IntPtr ExtraInfo;
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("Crypt32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CryptFindOIDInfo(
            int dwKeyType,
            IntPtr pvKey,
            int dwGroupId
        );

        public static string ResolveTemplateOid(string oid)
        {
            try
            {
                if (string.IsNullOrEmpty(oid))
                {
                    return string.Empty;
                }

                // Allocate memory for the OID string
                IntPtr pOid = Marshal.StringToHGlobalAnsi(oid);

                try
                {
                    // Look up the OID info
                    IntPtr pInfo = CryptFindOIDInfo(CRYPT_OID_INFO_OID_KEY, pOid, 0);

                    if (pInfo != IntPtr.Zero)
                    {
                        // Marshal the structure back
                        var info = Marshal.PtrToStructure<CRYPT_OID_INFO>(pInfo);
                        return info.pwszName ?? oid;
                    }

                    return oid;
                }
                finally
                {
                    // Free the allocated memory
                    Marshal.FreeHGlobal(pOid);
                }
            }
            catch (Exception)
            {
                return oid;
            }
        }
    }

    public enum CertViewSeek
    {
        CVR_SEEK_NONE = 0,
        CVR_SEEK_EQ = 1,
        CVR_SEEK_LT = 2,
        CVR_SEEK_LE = 4,
        CVR_SEEK_GE = 8,
        CVR_SEEK_GT = 16,
    }

    public enum CertViewSort
    {
        CVR_SORT_NONE = 0,
        CVR_SORT_ASCEND = 1,
        CVR_SORT_DESCEND = 2,
    }

    [SupportedOSPlatform("windows")]
    public class CertificatePollingService : ICertificatePollingService
    {
        private readonly ILogger<CertificatePollingService> _logger;
        private readonly ISocketMessageService _socketMessageService;
        private readonly Dictionary<string, Timer> _pollingTimers = new();
        private readonly Dictionary<string, bool> _isPolling = new();
        private readonly Dictionary<string, CancellationTokenSource> _pollCancellations = new();
        private readonly object _lockObject = new();
        private const string CERTVIEW_PROGID = "CertificateAuthority.View.1";
        public event EventHandler<CertificateProgress>? ProgressChanged;
        private readonly CertificateTrackingService _trackingService;
        private const int BatchSize = 1000;
        private readonly IConfigurationService _configService;

        public CertificatePollingService(
            ILogger<CertificatePollingService> logger,
            CertificateTrackingService trackingService,
            IConfigurationService configService,
            ISocketMessageService socketMessageService)
        {
            _logger = logger;
            _trackingService = trackingService;
            _configService = configService;
            _socketMessageService = socketMessageService;
        }

        public void ConfigurePolling(CertificateAuthorityConfig[] authorities)
        {
            lock (_lockObject)
            {
                var existingCAs = new HashSet<string>(_pollingTimers.Keys);
                var newCAs = new HashSet<string>(authorities.Select(a => a.Config));

                // Remove timers for CAs that are no longer in the configuration
                foreach (var ca in existingCAs.Except(newCAs))
                {
                    if (_pollingTimers.TryGetValue(ca, out var timer))
                    {
                        timer.Dispose();
                        _pollingTimers.Remove(ca);
                    }
                    if (_pollCancellations.TryGetValue(ca, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                        _pollCancellations.Remove(ca);
                    }
                    _isPolling.Remove(ca);
                }

                // Configure polling for each CA
                foreach (var ca in authorities)
                {
                    var interval = TimeSpan.FromSeconds(ca.SyncInterval);

                    // If this CA is already being polled, just update its timer
                    if (_isPolling.TryGetValue(ca.Config, out bool isPolling) && isPolling)
                    {
                        _logger.LogInformation("CA {ca} is currently being polled, updating timer only", ca.Config);

                        // Update existing timer with new interval
                        if (_pollingTimers.TryGetValue(ca.Config, out var existingTimer))
                        {
                            existingTimer.Change(interval, interval);
                        }
                        continue;
                    }

                    // Initialize state for new CA
                    _isPolling[ca.Config] = false;

                    // Create new cancellation token source only if one doesn't exist
                    if (!_pollCancellations.ContainsKey(ca.Config))
                    {
                        _pollCancellations[ca.Config] = new CancellationTokenSource();
                    }

                    _logger.LogInformation("Configuring certificate polling for CA {url} with interval {interval}s",
                        ca.Config, ca.SyncInterval);

                    var timer = new Timer(async _ => await TryStartPolling(ca),
                        null,
                        TimeSpan.Zero,  // Start immediately
                        interval);      // Then repeat at interval

                    // Update or add timer
                    if (_pollingTimers.ContainsKey(ca.Config))
                    {
                        _pollingTimers[ca.Config].Dispose();
                    }
                    _pollingTimers[ca.Config] = timer;
                }
            }
        }

        private async Task TryStartPolling(CertificateAuthorityConfig ca)
        {
            CancellationToken cancellationToken;

            // Check if polling is already in progress for this CA
            lock (_lockObject)
            {
                if (_isPolling.TryGetValue(ca.Config, out bool isPolling) && isPolling)
                {
                    _logger.LogWarning("Skipping poll for {ca} - previous poll still in progress", ca.Config);
                    return;
                }

                // Get cancellation token for this CA
                if (!_pollCancellations.TryGetValue(ca.Config, out var cts))
                {
                    _logger.LogWarning("No cancellation token found for {ca} - skipping poll", ca.Config);
                    return;
                }

                cancellationToken = cts.Token;
                _isPolling[ca.Config] = true;
            }

            try
            {
                await PollCertificates(ca, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Poll cancelled for {ca}", ca.Config);
            }
            finally
            {
                lock (_lockObject)
                {
                    _isPolling[ca.Config] = false;
                }
            }
        }

        private void ReportProgress(string message, int processed, int total, string caName, bool isComplete = false, string? error = null)
        {
            var progress = new CertificateProgress
            {
                Message = message,
                ProcessedCount = processed,
                TotalCount = total,
                CaName = caName,
                IsComplete = isComplete,
                Error = error
            };

            ProgressChanged?.Invoke(this, progress);
            _logger.LogInformation("[{ca}] {message} ({processed}/{total})", caName, message, processed, total);
        }

        private async Task PollCertificates(CertificateAuthorityConfig ca, CancellationToken cancellationToken)
        {
            try
            {
                // First retry any failed uploads
                await RetryFailedUploads(ca);

                // Then proceed with normal polling
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? certView = null;
                dynamic? view = null;
                var processedCount = 0;
                var totalCount = 0;
                var retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries)
                {
                    try
                    {
                        ReportProgress("Starting certificate poll...", 0, 0, ca.Config);

                        // Create CertView object
                        Type? certViewType = Type.GetTypeFromProgID(CERTVIEW_PROGID);
                        if (certViewType == null)
                        {
                            throw new InvalidOperationException("Could not get type for CertificateAuthority.View.1");
                        }

                        certView = Activator.CreateInstance(certViewType);
                        if (certView == null)
                        {
                            throw new InvalidOperationException("Failed to create CertView instance");
                        }

                        // Setup connection and view
                        ReportProgress("Connecting to CA...", 0, 0, ca.Config);
                        certView.OpenConnection(ca.Config);
                        certView.SetTable(0);

                        var notAfterColumnIndex = certView.GetColumnIndex(false, "NotAfter");
                        var requestIdColumnIndex = certView.GetColumnIndex(false, "RequestID");

                        var now = DateTime.Now;

                        certView.SetRestriction(
                            notAfterColumnIndex,
                            CertViewSeek.CVR_SEEK_GE,
                            CertViewSort.CVR_SORT_NONE,
                            now
                        );

                        string[] columnsToLoad =
                        {
                            "RequestId",
                            "NotAfter",
                            "Request.RequesterName",
                            "Request.RevokedWhen",
                            "Request.RevokedReason",
                            "CommonName",
                            "CertificateTemplate"
                        };

                        certView.SetResultColumnCount(columnsToLoad.Length);

                        foreach (var column in columnsToLoad)
                        {
                            var colIndex = certView.GetColumnIndex(false, column);
                            certView.SetResultColumn(colIndex);
                        }

                        view = certView.OpenView();
                        var certificates = new List<Certificate>();

                        ReportProgress("Processing certificates...", 0, 0, ca.Config);

                        var lastProcessedId = await _trackingService.GetLastProcessedRequestId(ca.Config);
                        _logger.LogInformation("Starting poll from RequestId {id} for CA {ca}", lastProcessedId, ca.Config);

                        // Set restriction to start from last processed ID
                        certView.SetRestriction(
                            requestIdColumnIndex,
                            CertViewSeek.CVR_SEEK_GT,
                            CertViewSort.CVR_SORT_ASCEND,
                            lastProcessedId
                        );

                        var currentBatch = new List<Certificate>();

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            int result = view.Next();
                            if (result == -1) break;

                            totalCount++;

                            using (var scope = new CertificateRowScope(view))
                            {
                                var cert = scope.ProcessCertificateRow();
                                if (cert != null)
                                {
                                    currentBatch.Add(cert);
                                    processedCount++;

                                    // When batch is full, send to API and mark as processed
                                    if (currentBatch.Count >= BatchSize)
                                    {
                                        await ProcessBatch(ca.Config, currentBatch);
                                        currentBatch.Clear();
                                    }

                                    if (processedCount % 100 == 0)
                                    {
                                        ReportProgress("Processing certificates...", processedCount, totalCount, ca.Config);
                                    }
                                }
                            }
                        }

                        // Process any remaining certificates
                        if (currentBatch.Count > 0)
                        {
                            await ProcessBatch(ca.Config, currentBatch);
                        }

                        ReportProgress($"Retrieved {processedCount} certificates", processedCount, totalCount, ca.Config, true);

                        await _socketMessageService.SendMessageAsync("status-update", "sleeping");

                        break; // Success, exit retry loop
                    }
                    catch (COMException ex) when ((uint)ex.HResult == 0x8009400F) // CERTSRV_E_NO_DB_SESSIONS
                    {
                        retryCount++;
                        _logger.LogWarning("CA session limit reached (attempt {attempt}/{maxAttempts}). Waiting before retry...",
                            retryCount, maxRetries);

                        ReportProgress($"CA session limit reached. Retry {retryCount}/{maxRetries}...",
                            processedCount, totalCount, ca.Config);

                        if (retryCount < maxRetries)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30 * retryCount)); // Increasing delay between retries
                        }
                        else
                        {
                            _logger.LogError(ex, "Failed to connect to CA after {maxRetries} attempts", maxRetries);
                            ReportProgress("Error polling certificates", processedCount, totalCount, ca.Config, true,
                                "CA session limit reached. Maximum retries exceeded.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error polling certificates from CA {url}", ca.Config);
                        ReportProgress("Error polling certificates", processedCount, totalCount, ca.Config, true, ex.Message);
                        break;
                    }
                    finally
                    {
                        // Ensure proper COM object cleanup
                        if (view != null)
                        {
                            try
                            {
                                Marshal.FinalReleaseComObject(view);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error releasing view object");
                            }
                            view = null;
                        }
                        if (certView != null)
                        {
                            try
                            {
                                Marshal.FinalReleaseComObject(certView);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error releasing certView object");
                            }
                            certView = null;
                        }

                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private async Task ProcessBatch(string caName, List<Certificate> certificates)
        {
            try
            {
                // Mark certificates as being processed
                await _trackingService.MarkCertificatesForProcessing(caName, certificates);

                // Get current configuration
                var config = await _configService.GetConfigAsync();
                var apiUrl = new Uri(new Uri(config.Url), "/api/ms-ca/certificates/bulk").ToString();

                // Send to API
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);

                _logger.LogInformation("Sending batch of {count} certificates to {url}", certificates.Count, apiUrl);

                var response = await client.PostAsJsonAsync(
                    apiUrl,
                    new { certificates = certificates });

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully uploaded batch of {count} certificates", certificates.Count);
                    // Mark only successful certificates
                    await _trackingService.MarkCertificatesAsUploaded(
                        caName,
                        certificates.Select(c => c.RequestId));
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("API returned error: {error}", error);
                    await _trackingService.MarkCertificatesAsFailed(
                        caName,
                        certificates.Select(c => c.RequestId),
                        error);
                    throw new Exception($"API returned error: {error}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing batch of {count} certificates", certificates.Count);
                await _trackingService.MarkCertificatesAsFailed(
                    caName,
                    certificates.Select(c => c.RequestId),
                    ex.Message);
                throw;
            }
        }

        private class CertificateRowScope : IDisposable
        {
            private readonly dynamic _view;
            private dynamic? _row;
            private bool _disposed;

            public CertificateRowScope(dynamic view)
            {
                _view = view;
                _row = _view.EnumCertViewColumn();
            }

            public Certificate? ProcessCertificateRow()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(CertificateRowScope));
                }

                try
                {
                    var certData = new Dictionary<string, object>();

                    while (true)
                    {
                        int colResult = _row?.Next() ?? -1;
                        if (colResult == -1) break;

                        string name = _row?.GetName() ?? string.Empty;
                        if (string.IsNullOrEmpty(name)) continue;

                        object? value = null;
                        try
                        {
                            value = _row?.GetValue(0);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (value != null)
                        {
                            certData[name] = value;
                        }
                    }

                    return certData.Count > 0 ? MapToCertificate(certData) : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static Certificate MapToCertificate(Dictionary<string, object> certData)
            {
                var cert = new Certificate();

                T GetValue<T>(string key, T defaultValue = default!) where T : class
                {
                    return certData.TryGetValue(key, out var value) ?
                        Convert.ToString(value) as T ?? defaultValue : defaultValue;
                }

                DateTime? GetDateTime(string key)
                {
                    if (!certData.TryGetValue(key, out var value)) return null;
                    return DateTime.TryParse(Convert.ToString(value), out var dt) ? dt : null;
                }

                int? GetInt(string key)
                {
                    if (!certData.TryGetValue(key, out var value)) return null;
                    return int.TryParse(Convert.ToString(value), out var num) ? num : null;
                }

                cert.CommonName = GetValue<string>("CommonName", string.Empty);
                cert.NotAfter = GetDateTime("NotAfter") ?? DateTime.MaxValue;
                var templateOid = GetValue<string>("CertificateTemplate", string.Empty);
                cert.TemplateName = OidResolver.ResolveTemplateOid(templateOid);
                cert.RequestId = GetInt("RequestID") ?? 0;
                cert.RequesterName = GetValue<string>("Request.RequesterName", string.Empty);
                cert.RevokedWhen = GetDateTime("Request.RevokedWhen");
                cert.Status = cert.RevokedWhen.HasValue ? "Revoked" : "Issued";

                return cert;
            }

            public void Dispose()
            {
                if (!_disposed && _row != null)
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(_row);
                    }
                    catch (Exception)
                    {
                        // Log if needed
                    }
                    finally
                    {
                        _row = null;
                        _disposed = true;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                foreach (var cts in _pollCancellations.Values)
                {
                    try
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                    catch { }
                }

                foreach (var timer in _pollingTimers.Values)
                {
                    try
                    {
                        timer.Dispose();
                    }
                    catch { }
                }

                _pollingTimers.Clear();
                _isPolling.Clear();
                _pollCancellations.Clear();
            }
        }

        private async Task RetryFailedUploads(CertificateAuthorityConfig ca)
        {
            try
            {
                var failedCertificates = await _trackingService.GetFailedUploads(ca.Config);
                if (!failedCertificates.Any())
                {
                    return;
                }

                _logger.LogInformation("Retrying {count} failed uploads for CA {ca}",
                    failedCertificates.Count(), ca.Config);

                foreach (var batch in failedCertificates.Chunk(BatchSize))
                {
                    await ProcessBatch(ca.Config, batch.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrying failed uploads for CA {ca}", ca.Config);
            }
        }
    }
}