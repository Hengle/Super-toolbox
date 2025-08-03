using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class SonyGxtExtractor : BaseExtractor
    {
        private readonly object _lockObject = new object();
        private int _extractedFileCount = 0;
        private int _processedFiles = 0;

        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionProgress;

        private static readonly byte[] GXT_HEADER = { 0x47, 0x58, 0x54, 0x00, 0x03, 0x00, 0x00, 0x10 };
        private const int BufferSize = 8192;

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

            string extractedDir = Path.Combine(directoryPath, "Extracted");
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
                    Interlocked.Increment(ref _processedFiles);
                    ExtractionProgress?.Invoke(this, $"处理文件 {_processedFiles}/{files.Length}: {Path.GetFileName(file)}");

                    if (Path.GetExtension(file).Equals(".gxt", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

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

            OnExtractionCompleted();
            ExtractionProgress?.Invoke(this, $"提取完成: 共找到 {_extractedFileCount} 个GXT文件");
        }

        private async Task ProcessFileAsync(string filePath, string destinationFolder, CancellationToken cancellationToken)
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            string filePrefix = Path.GetFileNameWithoutExtension(filePath);

            long position = 0;
            long fileSize = fileStream.Length;
            byte[] buffer = new byte[BufferSize];
            byte[] leftover = Array.Empty<byte>();

            while (position < fileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(BufferSize, fileSize - position);
                int bytesRead = await fileStream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                if (bytesRead == 0) break;

                byte[] currentData;
                if (leftover.Length > 0)
                {
                    currentData = new byte[leftover.Length + bytesRead];
                    Array.Copy(leftover, 0, currentData, 0, leftover.Length);
                    Array.Copy(buffer, 0, currentData, leftover.Length, bytesRead);
                }
                else
                {
                    currentData = new byte[bytesRead];
                    Array.Copy(buffer, 0, currentData, 0, bytesRead);
                }

                int headerPos = IndexOf(currentData, GXT_HEADER);
                if (headerPos != -1)
                {
                    long headerFilePos = position - leftover.Length + headerPos;

                    long gxtSize = await DetermineGxtSize(fileStream, headerFilePos, cancellationToken);
                    if (gxtSize > 0)
                    {
                        await ExtractGxtFile(fileStream, headerFilePos, gxtSize, destinationFolder, filePrefix);
                        position = headerFilePos + gxtSize;
                        fileStream.Seek(position, SeekOrigin.Begin);
                        leftover = Array.Empty<byte>();
                        continue;
                    }
                }

                leftover = currentData.Length > GXT_HEADER.Length
                    ? currentData[^GXT_HEADER.Length..]
                    : currentData;

                position += bytesRead;
            }
        }

        private async Task<long> DetermineGxtSize(FileStream fileStream, long headerPosition, CancellationToken cancellationToken)
        {
            const int maxSearchSize = 50 * 1024 * 1024;
            long fileSize = fileStream.Length;
            long searchEnd = Math.Min(headerPosition + maxSearchSize, fileSize);

            byte[] buffer = new byte[BufferSize];
            byte[] searchPattern = GXT_HEADER;
            byte[] leftover = Array.Empty<byte>();

            fileStream.Seek(headerPosition, SeekOrigin.Begin);
            long currentPos = headerPosition;

            while (currentPos < searchEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(BufferSize, searchEnd - currentPos);
                int bytesRead = await fileStream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                if (bytesRead == 0) break;

                byte[] currentData;
                if (leftover.Length > 0)
                {
                    currentData = new byte[leftover.Length + bytesRead];
                    Array.Copy(leftover, 0, currentData, 0, leftover.Length);
                    Array.Copy(buffer, 0, currentData, leftover.Length, bytesRead);
                }
                else
                {
                    currentData = new byte[bytesRead];
                    Array.Copy(buffer, 0, currentData, 0, bytesRead);
                }

                int nextHeaderPos = IndexOf(currentData, searchPattern);
                if (nextHeaderPos != -1 && (currentPos - leftover.Length + nextHeaderPos) > headerPosition)
                {
                    return (currentPos - leftover.Length + nextHeaderPos) - headerPosition;
                }

                leftover = currentData.Length > searchPattern.Length
                    ? currentData[^searchPattern.Length..]
                    : currentData;

                currentPos += bytesRead;
            }

            return fileSize - headerPosition;
        }

        private async Task ExtractGxtFile(FileStream sourceStream, long startPosition, long length, string destinationFolder, string filePrefix)
        {
            string newFileName;
            string filePath;

            lock (_lockObject)
            {
                int fileCount = Directory.GetFiles(destinationFolder, "*.gxt").Length;
                newFileName = $"{filePrefix}_{fileCount}.gxt";
                filePath = Path.Combine(destinationFolder, newFileName);
            }

            try
            {
                using var outputStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                sourceStream.Seek(startPosition, SeekOrigin.Begin);

                byte[] buffer = new byte[BufferSize];
                long bytesRemaining = length;
                long totalBytesWritten = 0;

                while (bytesRemaining > 0)
                {
                    int bytesToRead = (int)Math.Min(bytesRemaining, BufferSize);
                    int bytesRead = await sourceStream.ReadAsync(buffer, 0, bytesToRead);
                    if (bytesRead == 0) break;

                    await outputStream.WriteAsync(buffer, 0, bytesRead);
                    bytesRemaining -= bytesRead;
                    totalBytesWritten += bytesRead;
                }

                if (totalBytesWritten == length)
                {
                    Interlocked.Increment(ref _extractedFileCount);
                    FileExtracted?.Invoke(this, $"已提取: {newFileName} (大小: {length} 字节)");
                    OnFileExtracted(filePath);
                }
                else
                {
                    ExtractionProgress?.Invoke(this, $"文件 {newFileName} 提取不完整 (预期: {length} 字节, 实际: {totalBytesWritten} 字节)");
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                ExtractionProgress?.Invoke(this, $"保存文件 {newFileName} 时出错: {ex.Message}");
                if (File.Exists(filePath)) File.Delete(filePath);
            }
        }

        private static int IndexOf(byte[] source, byte[] pattern)
        {
            for (int i = 0; i <= source.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}