using System;
using Shared.Models;

namespace Shared.Services
{
    public interface ICertificatePollingService : IDisposable
    {
        void ConfigurePolling(CertificateAuthorityConfig[] authorities);
        event EventHandler<CertificateProgress> ProgressChanged;
    }
} 