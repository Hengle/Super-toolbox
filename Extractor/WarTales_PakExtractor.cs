using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class WarTales_PakExtractor : BaseExtractor
    {
        public event EventHandler<List<string>>? FilesExtracted;
        public event EventHandler<string>? ExtractionStarted;
        public event EventHandler<string>? ExtractionProgress;
        public event EventHandler<string>? ExtractionError;

        public override void Extract(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return;
            ExtractAsync(directoryPath).Wait();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(directoryPath)) return;

            List<string> extractedFiles = new List<string>();
            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"源文件夹 {directoryPath} 不存在");
                OnExtractionFailed($"源文件夹 {directoryPath} 不存在");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始处理目录: {directoryPath}");

            var filePaths = Directory.EnumerateFiles(directoryPath, "*.pak", SearchOption.TopDirectoryOnly)
                .Where(file => !file.StartsWith(extractedDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            TotalFilesToExtract = filePaths.Count;

            foreach (var filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExtractionProgress?.Invoke(this, $"正在处理文件: {Path.GetFileName(filePath)}");

                try
                {
                    string outputPath = Path.Combine(extractedDir, Path.GetFileNameWithoutExtension(filePath));
                    await Task.Run(() =>
                    {
                        PakExtractor.UnpackPakFile(filePath, outputPath);
                    }, cancellationToken);

                    var files = Directory.EnumerateFiles(outputPath, "*.*", SearchOption.AllDirectories).ToList();
                    extractedFiles.AddRange(files);
                    foreach (var file in files)
                    {
                        OnFileExtracted(file);
                    }
                }
                catch (OperationCanceledException)
                {
                    ExtractionError?.Invoke(this, "提取操作已取消");
                    OnExtractionFailed("提取操作已取消");
                    throw;
                }
                catch (Exception e)
                {
                    ExtractionError?.Invoke(this, $"处理文件 {Path.GetFileName(filePath)} 时出错: {e.Message}");
                    OnExtractionFailed($"处理文件 {Path.GetFileName(filePath)} 时出错: {e.Message}");
                }
            }

            int realCount = extractedFiles.Count;
            if (realCount > 0)
            {
                FilesExtracted?.Invoke(this, extractedFiles);
                ExtractionProgress?.Invoke(this, $"处理完成，共提取 {realCount} 个文件");
            }
            else
            {
                ExtractionProgress?.Invoke(this, "处理完成，未找到可提取的文件");
            }

            OnExtractionCompleted();
        }

        protected new virtual void OnFileExtracted(string filePath) { }
        protected new virtual void OnExtractionFailed(string message) { }
        protected new virtual void OnExtractionCompleted() { }
        public new int TotalFilesToExtract { get; private set; }
    }
}