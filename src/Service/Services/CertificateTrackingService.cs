using Microsoft.Extensions.Logging;
using System.Data.SQLite;
using Dapper;
using Shared.Models;
using Shared;

namespace Service.Services
{
    public class CertificateTrackingService : IDisposable
    {
        private readonly ILogger<CertificateTrackingService> _logger;
        private readonly string _databasePath;
        private readonly object _connectionLock = new();

        public CertificateTrackingService(ILogger<CertificateTrackingService> logger)
        {
            _logger = logger;
            _databasePath = Path.Combine(Constants.AppDataPath, "tracking", "certificate_tracking.db");

            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            
            using var connection = new SQLiteConnection($"Data Source={_databasePath};");
            connection.Open();
            
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS ProcessedCertificates (
                    RequestId INTEGER,
                    CaName TEXT NOT NULL,
                    ProcessedAt TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    UploadStatus TEXT NOT NULL,
                    RetryCount INTEGER DEFAULT 0,
                    LastError TEXT,
                    PRIMARY KEY (RequestId, CaName)
                );
                
                CREATE INDEX IF NOT EXISTS idx_ca_name ON ProcessedCertificates (CaName);
                CREATE INDEX IF NOT EXISTS idx_upload_status ON ProcessedCertificates (UploadStatus);
            ");
        }

        public async Task<IEnumerable<Certificate>> GetFailedUploads(string caName, int maxRetries = 3)
        {
            using var connection = new SQLiteConnection($"Data Source={_databasePath};");
            var results = await connection.QueryAsync<CertificateRecord>(
                @"SELECT * FROM ProcessedCertificates 
                WHERE CaName = @CaName 
                AND UploadStatus = 'Failed' 
                AND RetryCount < @MaxRetries",
                new { CaName = caName, MaxRetries = maxRetries });

            return results.Select(r => new Certificate
            {
                RequestId = r.RequestId,
                Status = r.Status,
                // ... map other properties if needed
            });
        }

        public async Task MarkCertificatesForProcessing(string caName, IEnumerable<Certificate> certificates)
        {
            using var connection = new SQLiteConnection($"Data Source={_databasePath};");
            await connection.ExecuteAsync(@"
                INSERT OR REPLACE INTO ProcessedCertificates 
                (RequestId, CaName, ProcessedAt, Status, UploadStatus, RetryCount, LastError)
                VALUES 
                (@RequestId, @CaName, @ProcessedAt, @Status, 'Pending', 0, NULL)",
                certificates.Select(c => new
                {
                    RequestId = c.RequestId,
                    CaName = caName,
                    ProcessedAt = DateTime.UtcNow.ToString("O"),
                    Status = c.Status
                }));
        }

        public async Task MarkCertificatesAsUploaded(string caName, IEnumerable<int> requestIds)
        {
            using var connection = new SQLiteConnection($"Data Source={_databasePath};");
            await connection.ExecuteAsync(@"
                UPDATE ProcessedCertificates 
                SET UploadStatus = 'Success', 
                    ProcessedAt = @Now
                WHERE CaName = @CaName 
                AND RequestId IN @RequestIds",
                new 
                { 
                    CaName = caName, 
                    RequestIds = requestIds,
                    Now = DateTime.UtcNow.ToString("O")
                });
        }

        public async Task MarkCertificatesAsFailed(string caName, IEnumerable<int> requestIds, string error)
        {
            using var connection = new SQLiteConnection($"Data Source={_databasePath};");
            await connection.ExecuteAsync(@"
                UPDATE ProcessedCertificates 
                SET UploadStatus = 'Failed', 
                    RetryCount = RetryCount + 1,
                    LastError = @Error,
                    ProcessedAt = @Now
                WHERE CaName = @CaName 
                AND RequestId IN @RequestIds",
                new 
                { 
                    CaName = caName, 
                    RequestIds = requestIds,
                    Error = error,
                    Now = DateTime.UtcNow.ToString("O")
                });
        }

        public async Task<int> GetLastProcessedRequestId(string caName)
        {
            using var connection = new SQLiteConnection($"Data Source={_databasePath};");
            var result = await connection.QueryFirstOrDefaultAsync<int?>(
                @"SELECT MAX(RequestId) 
                FROM ProcessedCertificates 
                WHERE CaName = @CaName 
                AND UploadStatus = 'Success'",  // Only consider successfully uploaded certificates
                new { CaName = caName });
            
            return result ?? 0;
        }

        private class CertificateRecord
        {
            public int RequestId { get; set; }
            public string CaName { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string UploadStatus { get; set; } = string.Empty;
            public int RetryCount { get; set; }
            public string? LastError { get; set; }
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
} 