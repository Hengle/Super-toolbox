using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class PngExtractor : BaseExtractor
    {
        private readonly object _lockObject = new object();
        private new int _extractedFileCount = 0;

        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionProgress;
        public new event EventHandler<int>? ExtractionCompleted;

        private static readonly byte[] START_SEQUENCE = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] END_SEQUENCE = { 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                ExtractionProgress?.Invoke(this, $"错误: {directoryPath} 不是有效的目录");
                OnExtractionFailed($"错误: {directoryPath} 不是有效的目录");
                return;
            }

            var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
            TotalFilesToExtract = files.Length;
            ExtractionProgress?.Invoke(this, $"开始处理 {files.Length} 个文件...");
            OnFileExtracted($"开始处理 {files.Length} 个文件...");

            string extractedDir = Path.Combine(directoryPath, "extracted");
            if (!Directory.Exists(extractedDir))
            {
                Directory.CreateDirectory(extractedDir);
                ExtractionProgress?.Invoke(this, $"创建文件夹: {extractedDir}");
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ProcessFileAsync(file, extractedDir, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ExtractionProgress?.Invoke(this, "提取操作已取消");
                    OnExtractionFailed("提取操作已取消");
                    throw;
                }
                catch (Exception ex)
                {
                    ExtractionProgress?.Invoke(this, $"处理文件 {file} 时出错: {ex.Message}");
                    OnExtractionFailed($"处理文件 {file} 时出错: {ex.Message}");
                }
            }

            ExtractionCompleted?.Invoke(this, _extractedFileCount);
            OnExtractionCompleted();
            ExtractionProgress?.Invoke(this, $"提取完成: 共找到 {_extractedFileCount} 个PNG文件");
        }

        private async Task ProcessFileAsync(string filePath, string destinationFolder, CancellationToken cancellationToken)
        {
            byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
            string fileName = Path.GetFileName(filePath);
            string filePrefix = Path.GetFileNameWithoutExtension(fileName);

            int startIndex = 0;
            int fileCount = 0;

            while ((startIndex = FindSequence(content, START_SEQUENCE, startIndex)) != -1)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int endIndex = FindSequence(content, END_SEQUENCE, startIndex);
                if (endIndex == -1) break;

                endIndex += END_SEQUENCE.Length;
                byte[] pngData = new byte[endIndex - startIndex];
                Array.Copy(content, startIndex, pngData, 0, pngData.Length);

                if (IsValidPng(pngData))
                {
                    fileCount++;
                    SavePngFile(pngData, destinationFolder, filePrefix, fileCount);
                }

                startIndex = endIndex;
            }
        }

        private void SavePngFile(byte[] pngData, string destinationFolder, string filePrefix, int fileCount)
        {
            string newFileName = $"{filePrefix}_{fileCount}.png";
            string filePath = Path.Combine(destinationFolder, newFileName);

            try
            {
                File.WriteAllBytes(filePath, pngData);

                int count = Interlocked.Increment(ref _extractedFileCount);

                FileExtracted?.Invoke(this, $"已提取: {newFileName}");
                OnFileExtracted(filePath);
            }
            catch (Exception ex)
            {
                ExtractionProgress?.Invoke(this, $"保存文件 {newFileName} 时出错: {ex.Message}");
            }
        }

        private static int FindSequence(byte[] content, byte[] sequence, int startIndex)
        {
            for (int i = startIndex; i <= content.Length - sequence.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < sequence.Length; j++)
                {
                    if (content[i + j] != sequence[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static bool IsValidPng(byte[] data)
        {
            if (data.Length < 8) return false;
            
            for (int i = 0; i < START_SEQUENCE.Length; i++)
            {
                if (data[i] != START_SEQUENCE[i]) return false;
            }
            
            return true;
        }
    }
}    