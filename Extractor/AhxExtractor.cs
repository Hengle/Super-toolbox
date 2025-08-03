using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class AhxExtractor : BaseExtractor
    {
        private static readonly byte[] AHX_START_HEADER = { 0x80, 0x00, 0x00, 0x20 };
        private static readonly byte[] AHX_END_HEADER = { 0x80, 0x01, 0x00, 0x0C, 0x41, 0x48, 0x58, 0x45, 0x28, 0x63, 0x29, 0x43, 0x52, 0x49, 0x00, 0x00 };

        public event EventHandler<List<string>>? FilesExtracted;
        public event EventHandler<string>? ExtractionStarted;
        public event EventHandler<string>? ExtractionProgress;
        public event EventHandler<string>? ExtractionError;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            List<string> allExtractedFileNames = new List<string>();

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"目录不存在: {directoryPath}");
                OnExtractionFailed($"目录不存在: {directoryPath}");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始从目录 {directoryPath} 提取AHX文件");

            try
            {
                var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
                TotalFilesToExtract = await Task.Run(() => files is ICollection<string> collection ? collection.Count : 0);

                foreach (string filePath in files)
                {
                    ThrowIfCancellationRequested(cancellationToken);

                    if (Path.GetExtension(filePath).Equals(".ahx", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ExtractionProgress?.Invoke(this, $"处理进度: {ExtractedFileCount}/{TotalFilesToExtract} - {Path.GetFileName(filePath)}");

                    try
                    {
                        var fileNames = await ExtractAhxsFromFileAsync(filePath, cancellationToken);
                        allExtractedFileNames.AddRange(fileNames);
                        OnFileExtracted(filePath);
                    }
                    catch (Exception ex)
                    {
                        ExtractionError?.Invoke(this, $"处理文件 {filePath} 时出错: {ex.Message}");
                        OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                    }
                }

                FilesExtracted?.Invoke(this, allExtractedFileNames);
                ExtractionProgress?.Invoke(this, $"提取完成，共提取 {allExtractedFileNames.Count} 个AHX文件");
            }
            catch (OperationCanceledException)
            {
                ExtractionError?.Invoke(this, "提取操作已取消");
                OnExtractionFailed("提取操作已取消");
                throw;
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"提取过程中发生错误: {ex.Message}");
                OnExtractionFailed($"提取过程中发生错误: {ex.Message}");
            }
        }

        private async Task<List<string>> ExtractAhxsFromFileAsync(string filePath, CancellationToken cancellationToken)
        {
            List<string> extractedFileNames = new List<string>();

            try
            {
                ExtractionProgress?.Invoke(this, $"正在处理文件: {Path.GetFileName(filePath)}");

                byte[] fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
                int count = 0;

                foreach (byte[] ahxData in ExtractAhxData(fileContent))
                {
                    ThrowIfCancellationRequested(cancellationToken);

                    string baseFilename = Path.GetFileNameWithoutExtension(filePath);
                    string extractedFilename = $"{baseFilename}_{count}.ahx";
                    string extractedPath = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, extractedFilename);

                    string? dirToCreate = Path.GetDirectoryName(extractedPath);
                    if (dirToCreate != null && !Directory.Exists(dirToCreate))
                    {
                        Directory.CreateDirectory(dirToCreate);
                    }

                    await File.WriteAllBytesAsync(extractedPath, ahxData, cancellationToken);
                    ExtractionProgress?.Invoke(this, $"已提取: {extractedPath}");
                    extractedFileNames.Add(extractedPath);
                    count++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"处理文件 {filePath} 时出错: {ex.Message}");
                throw;
            }

            return extractedFileNames;
        }

        private static IEnumerable<byte[]> ExtractAhxData(byte[] fileContent)
        {
            int startIndex = 0;
            while ((startIndex = IndexOf(fileContent, AHX_START_HEADER, startIndex)) != -1)
            {
                int endIndex = IndexOf(fileContent, AHX_END_HEADER, startIndex + AHX_START_HEADER.Length);
                if (endIndex != -1)
                {
                    endIndex += AHX_END_HEADER.Length;
                    int length = endIndex - startIndex;
                    byte[] ahxData = new byte[length];
                    Array.Copy(fileContent, startIndex, ahxData, 0, length);
                    yield return ahxData;
                    startIndex = endIndex;
                }
                else
                {
                    break;
                }
            }
        }

        private static int IndexOf(byte[] source, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
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
    }
}