using System;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public abstract class BaseExtractor
    {
        public event EventHandler<string>? FileExtracted;
        public event EventHandler<int>? ProgressUpdated;
        public event EventHandler<int>? ExtractionCompleted;
        public event EventHandler<string>? ExtractionFailed;

        public int ExtractedFileCount { get; private set; } = 0;
        public int TotalFilesToExtract { get; protected set; } = 0;
        public int ProgressPercentage => TotalFilesToExtract > 0
            ? (int)((ExtractedFileCount / (double)TotalFilesToExtract) * 100)
            : 0;

        public bool IsCancellationRequested { get; private set; } = false;

        private int _extractedFileCount;

        public abstract Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default);

        public virtual void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        protected void OnFileExtracted(string fileName)
        {
            Interlocked.Increment(ref _extractedFileCount);
            ExtractedFileCount = _extractedFileCount;
            FileExtracted?.Invoke(this, fileName);
            ProgressUpdated?.Invoke(this, ProgressPercentage);

            if (ExtractedFileCount == TotalFilesToExtract)
            {
                OnExtractionCompleted();
            }
        }

        protected void OnExtractionCompleted()
        {
            ExtractionCompleted?.Invoke(this, ExtractedFileCount);
        }

        protected void OnExtractionFailed(string errorMessage)
        {
            ExtractionFailed?.Invoke(this, errorMessage);
        }

        protected void ThrowIfCancellationRequested(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || IsCancellationRequested)
            {
                IsCancellationRequested = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}