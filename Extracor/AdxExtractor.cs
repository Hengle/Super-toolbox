using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class AdxExtractor : BaseExtractor
    {
        public event EventHandler<List<string>>? FilesExtracted;
        public event EventHandler<string>? ExtractionStarted;
        public event EventHandler<string>? ExtractionProgress;
        public event EventHandler<string>? ExtractionError;
        public new event EventHandler<string>? ExtractionCompleted;

        private static readonly byte[] ADX_SIG_BYTES = { 0x80, 0x00 };
        private static readonly byte[] CRI_COPYRIGHT_BYTES = { 0x28, 0x63, 0x29, 0x43, 0x52, 0x49 };
        private static readonly byte[][] FIXED_SEQUENCES =
        {
            new byte[] { 0x03, 0x12, 0x04, 0x01, 0x00, 0x00 },
            new byte[] { 0x03, 0x12, 0x04, 0x02, 0x00, 0x00 }
        };

        private static int IndexOf(byte[] data, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= data.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool ContainsBytes(byte[] data, byte[] pattern)
        {
            return IndexOf(data, pattern, 0) != -1;
        }

        public override void Extract(string directoryPath)
        {
            List<string> extractedFiles = new List<string>();
            int fileCount = 0;

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"源文件夹 {directoryPath} 不存在");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始处理目录: {directoryPath}");

            var filePaths = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
            int totalFiles = 0;
            int processedFiles = 0;

            foreach (var _ in filePaths) totalFiles++;

            TotalFilesToExtract = totalFiles;

            foreach (var filePath in filePaths)
            {
                processedFiles++;
                ExtractionProgress?.Invoke(this, $"正在处理文件 {processedFiles}/{totalFiles}: {Path.GetFileName(filePath)}");
                OnFileExtracted(filePath);

                try
                {
                    byte[] content = File.ReadAllBytes(filePath);
                    int index = 0;
                    int? currentHeaderStart = null;
                    int innerCount = 1;

                    while (index < content.Length)
                    {
                        int headerStartIndex = IndexOf(content, ADX_SIG_BYTES, index);
                        if (headerStartIndex == -1)
                        {
                            if (currentHeaderStart.HasValue)
                            {
                                var result = SaveExtractedFile(content, currentHeaderStart.Value, content.Length, filePath, innerCount, ref fileCount, extractedFiles);
                                if (result.Success)
                                {
                                    ExtractionProgress?.Invoke(this, $"已提取: {result.FileName}");
                                    innerCount++;
                                }
                            }
                            break;
                        }

                        int checkLength = Math.Min(10, content.Length - headerStartIndex);
                        var checkSegment = new byte[checkLength];
                        Array.Copy(content, headerStartIndex, checkSegment, 0, checkLength);

                        if (ContainsBytes(checkSegment, FIXED_SEQUENCES[0]) ||
                            ContainsBytes(checkSegment, FIXED_SEQUENCES[1]))
                        {
                            int nextHeaderIndex = IndexOf(content, ADX_SIG_BYTES, headerStartIndex + 1);
                            if (!currentHeaderStart.HasValue)
                            {
                                currentHeaderStart = headerStartIndex;
                            }
                            else
                            {
                                var result = SaveExtractedFile(content, currentHeaderStart.Value, headerStartIndex, filePath, innerCount, ref fileCount, extractedFiles);
                                if (result.Success)
                                {
                                    ExtractionProgress?.Invoke(this, $"已提取: {result.FileName}");
                                    innerCount++;
                                }
                                currentHeaderStart = headerStartIndex;
                            }
                        }

                        index = headerStartIndex + 1;
                    }
                }
                catch (IOException e)
                {
                    ExtractionError?.Invoke(this, $"读取文件 {filePath} 时出错: {e.Message}");
                }
            }

            FilesExtracted?.Invoke(this, extractedFiles);
            ExtractionCompleted?.Invoke(this, $"处理完成，共提取出 {fileCount} 个符合条件的文件片段");
            OnExtractionCompleted();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            List<string> extractedFiles = new List<string>();
            int fileCount = 0;

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"源文件夹 {directoryPath} 不存在");
                OnExtractionFailed($"源文件夹 {directoryPath} 不存在");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始处理目录: {directoryPath}");
            OnFileExtracted($"开始处理目录: {directoryPath}");

            var filePaths = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
            int totalFiles = 0;
            int processedFiles = 0;

            foreach (var _ in filePaths) totalFiles++;

            TotalFilesToExtract = totalFiles;

            foreach (var filePath in filePaths)
            {
                ThrowIfCancellationRequested(cancellationToken);

                processedFiles++;
                ExtractionProgress?.Invoke(this, $"正在处理文件 {processedFiles}/{totalFiles}: {Path.GetFileName(filePath)}");
                OnFileExtracted(filePath);

                try
                {
                    byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    int index = 0;
                    int? currentHeaderStart = null;
                    int innerCount = 1;

                    while (index < content.Length)
                    {
                        ThrowIfCancellationRequested(cancellationToken);

                        int headerStartIndex = IndexOf(content, ADX_SIG_BYTES, index);
                        if (headerStartIndex == -1)
                        {
                            if (currentHeaderStart.HasValue)
                            {
                                var result = SaveExtractedFile(content, currentHeaderStart.Value, content.Length, filePath, innerCount, ref fileCount, extractedFiles);
                                if (result.Success)
                                {
                                    ExtractionProgress?.Invoke(this, $"已提取: {result.FileName}");
                                }
                            }
                            break;
                        }

                        int checkLength = Math.Min(10, content.Length - headerStartIndex);
                        var checkSegment = new byte[checkLength];
                        Array.Copy(content, headerStartIndex, checkSegment, 0, checkLength);

                        if (ContainsBytes(checkSegment, FIXED_SEQUENCES[0]) ||
                            ContainsBytes(checkSegment, FIXED_SEQUENCES[1]))
                        {
                            int nextHeaderIndex = IndexOf(content, ADX_SIG_BYTES, headerStartIndex + 1);
                            if (!currentHeaderStart.HasValue)
                            {
                                currentHeaderStart = headerStartIndex;
                            }
                            else
                            {
                                var result = SaveExtractedFile(content, currentHeaderStart.Value, headerStartIndex, filePath, innerCount, ref fileCount, extractedFiles);
                                if (result.Success)
                                {
                                    ExtractionProgress?.Invoke(this, $"已提取: {result.FileName}");
                                    innerCount++;
                                }
                                currentHeaderStart = headerStartIndex;
                            }
                        }

                        index = headerStartIndex + 1;
                    }
                }
                catch (OperationCanceledException)
                {
                    ExtractionError?.Invoke(this, "提取操作已取消");
                    OnExtractionFailed("提取操作已取消");
                    throw;
                }
                catch (IOException e)
                {
                    ExtractionError?.Invoke(this, $"读取文件 {filePath} 时出错: {e.Message}");
                    OnExtractionFailed($"读取文件 {filePath} 时出错: {e.Message}");
                }
            }

            FilesExtracted?.Invoke(this, extractedFiles);
            ExtractionCompleted?.Invoke(this, $"处理完成，共提取出 {fileCount} 个符合条件的文件片段");
            OnExtractionCompleted();
        }

        private (bool Success, string FileName) SaveExtractedFile(byte[] content, int start, int end, string filePath, int innerCount, ref int fileCount, List<string> extractedFiles)
        {
            int length = end - start;
            byte[] searchRange = new byte[length];
            Array.Copy(content, start, searchRange, 0, length);

            if (ContainsBytes(searchRange, CRI_COPYRIGHT_BYTES))
            {
                string baseFileName = Path.GetFileNameWithoutExtension(filePath);
                string outputFileName;
                if (innerCount == 1)
                {
                    outputFileName = $"{baseFileName}.adx";
                }
                else
                {
                    outputFileName = $"{baseFileName}_{innerCount}.adx";
                }
                string outputFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, outputFileName);

                try
                {
                    File.WriteAllBytes(outputFilePath, searchRange);
                    extractedFiles.Add(outputFilePath);
                    Interlocked.Increment(ref fileCount);
                    return (true, outputFileName);
                }
                catch (IOException e)
                {
                    ExtractionError?.Invoke(this, $"写入文件 {outputFilePath} 时出错: {e.Message}");
                    return (false, string.Empty);
                }
            }

            return (false, string.Empty);
        }
    }
}