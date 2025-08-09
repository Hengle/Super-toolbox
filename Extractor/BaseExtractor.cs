using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public abstract class BaseExtractor
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        protected static extern bool SetDllDirectory(string lpPathName);

        protected static string TempDllDirectory { get; private set; } = string.Empty;

        static BaseExtractor()
        {
            InitializeDllLoading();
        }

        private static void InitializeDllLoading()
        {
            TempDllDirectory = Path.Combine(Path.GetTempPath(), "supertoolbox_temp");
            Directory.CreateDirectory(TempDllDirectory);
            SetDllDirectory(TempDllDirectory);

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { Directory.Delete(TempDllDirectory, true); } catch { }
            };
        }

        protected static void LoadEmbeddedDll(string embeddedResourceName, string dllFileName)
        {
            string dllPath = Path.Combine(TempDllDirectory, dllFileName);

            if (!File.Exists(dllPath))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(embeddedResourceName))
                {
                    if (stream == null)
                        throw new FileNotFoundException($"嵌入的DLL资源 '{embeddedResourceName}' 未找到");

                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    File.WriteAllBytes(dllPath, buffer);
                }
            }
        }

        protected static string LoadEmbeddedExe(string embeddedResourceName, string exeFileName)
        {
            string exePath = Path.Combine(TempDllDirectory, exeFileName);

            if (!File.Exists(exePath))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(embeddedResourceName))
                {
                    if (stream == null)
                        throw new FileNotFoundException($"嵌入的EXE资源 '{embeddedResourceName}' 未找到");

                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    File.WriteAllBytes(exePath, buffer);
                }
            }

            return exePath;
        }

        public event EventHandler<string>? FileExtracted;
        public event EventHandler<int>? ProgressUpdated;
        public event EventHandler<int>? ExtractionCompleted;
        public event EventHandler<string>? ExtractionFailed;

        private int _extractedFileCount = 0;
        private int _totalFilesToExtract = 0;
        private bool _isExtractionCompleted = false;
        private readonly object _lock = new object();

        public int ExtractedFileCount
        {
            get { lock (_lock) return _extractedFileCount; }
        }

        public int TotalFilesToExtract
        {
            get { lock (_lock) return _totalFilesToExtract; }
            protected set { lock (_lock) _totalFilesToExtract = value; }
        }

        public int ProgressPercentage
        {
            get
            {
                lock (_lock)
                {
                    return _totalFilesToExtract > 0
                        ? (int)((_extractedFileCount / (double)_totalFilesToExtract) * 100)
                        : 0;
                }
            }
        }

        public bool IsCancellationRequested { get; private set; } = false;

        public abstract Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default);

        public virtual void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        protected void OnFileExtracted(string fileName)
        {
            bool shouldTriggerCompleted = false;
            int currentCount;
            int currentTotal;

            lock (_lock)
            {
                _extractedFileCount++;
                currentCount = _extractedFileCount;
                currentTotal = _totalFilesToExtract;
                shouldTriggerCompleted = !_isExtractionCompleted && currentCount == currentTotal;
                if (shouldTriggerCompleted)
                {
                    _isExtractionCompleted = true;
                }
            }

            FileExtracted?.Invoke(this, fileName);
            ProgressUpdated?.Invoke(this, ProgressPercentage);

            if (shouldTriggerCompleted)
            {
                ExtractionCompleted?.Invoke(this, currentCount);
            }
        }

        protected void SetExtractedFileCount(int count)
        {
            bool shouldTriggerCompleted = false;
            int currentTotal;

            lock (_lock)
            {
                _extractedFileCount = count;
                currentTotal = _totalFilesToExtract;
                shouldTriggerCompleted = !_isExtractionCompleted && count == currentTotal;
                if (shouldTriggerCompleted)
                {
                    _isExtractionCompleted = true;
                }
            }

            ProgressUpdated?.Invoke(this, ProgressPercentage);

            if (shouldTriggerCompleted)
            {
                ExtractionCompleted?.Invoke(this, count);
            }
        }

        protected void OnExtractionCompleted()
        {
            lock (_lock)
            {
                if (!_isExtractionCompleted)
                {
                    _isExtractionCompleted = true;
                    ExtractionCompleted?.Invoke(this, _extractedFileCount);
                }
            }
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
