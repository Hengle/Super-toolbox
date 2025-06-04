using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class JpgExtractor : BaseExtractor
    {
        private readonly object _lockObject = new object();
        private new int _extractedFileCount = 0;

        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionProgress;
        public new event EventHandler<int>? ExtractionCompleted;

        private static readonly byte[] START_SEQUENCE = { 0xFF, 0xD8, 0xFF, 0xE0 };
        private static readonly byte[] JFIF_MARKER = System.Text.Encoding.ASCII.GetBytes("JFIF");
        private static readonly byte[] END_SEQUENCE = { 0xFF, 0xD9 };

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

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ProcessFileAsync(file, directoryPath, cancellationToken);
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
            ExtractionProgress?.Invoke(this, $"提取完成: 共找到 {_extractedFileCount} 个JPG文件");
        }

        private async Task ProcessFileAsync(string filePath, string baseDirectory, CancellationToken cancellationToken)
        {
            byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
            string fileName = Path.GetFileName(filePath);
            string destinationFolder = Path.Combine(baseDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}_extracted");

            if (!Directory.Exists(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
                ExtractionProgress?.Invoke(this, $"创建文件夹: {destinationFolder}");
            }

            int startIndex = 0;
            int fileCount = 0;

            while ((startIndex = FindSequence(content, START_SEQUENCE, startIndex)) != -1)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int endIndex = FindSequence(content, END_SEQUENCE, startIndex);
                if (endIndex == -1) break;

                endIndex += END_SEQUENCE.Length;
                byte[] jpegData = new byte[endIndex - startIndex];
                Array.Copy(content, startIndex, jpegData, 0, jpegData.Length);

                if (IsValidJpeg(jpegData, JFIF_MARKER))
                {
                    fileCount++;
                    SaveJpegFile(jpegData, destinationFolder, fileName, fileCount);
                }

                startIndex = endIndex;
            }
        }

        private void SaveJpegFile(byte[] jpegData, string destinationFolder, string sourceFileName, int fileCount)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourceFileName);
            string newFileName = $"{baseName}_{fileCount}.jpg";
            string filePath = Path.Combine(destinationFolder, newFileName);

            try
            {
                File.WriteAllBytes(filePath, jpegData);

                int count = Interlocked.Increment(ref _extractedFileCount);

                FileExtracted?.Invoke(this, $"已提取: {newFileName}");

                OnFileExtracted(newFileName);
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

        private static bool IsValidJpeg(byte[] data, byte[] jfifMarker)
        {
            return FindSequence(data, jfifMarker, 0) != -1;
        }
    }
}