using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class BankExtractor : BaseExtractor
    {
        private static readonly byte[] riffHeader = { 0x52, 0x49, 0x46, 0x46 };
        private static readonly byte[] bankBlock = { 0x46, 0x45, 0x56, 0x20, 0x46, 0x4D, 0x54 };

        public event EventHandler<List<string>>? FilesExtracted;
        public event EventHandler<string>? ExtractionStarted;
        public event EventHandler<string>? ExtractionProgress;
        public event EventHandler<string>? ExtractionError;
        public new event EventHandler<string>? ExtractionCompleted;
        private void OnProgressUpdated(int progressPercentage)
        {
            ExtractionProgress?.Invoke(this, $"提取进度: {progressPercentage}%");
        }

        private static List<byte[]> ExtractbankData(byte[] fileContent)
        {
            List<byte[]> bankDataList = new List<byte[]>();
            int bankDataStart = 0;
            while ((bankDataStart = IndexOf(fileContent, riffHeader, bankDataStart)) != -1)
            {
                try
                {
                    int fileSize = BitConverter.ToInt32(fileContent, bankDataStart + 4);
                    fileSize = (fileSize + 1) & ~1;

                    int blockStart = bankDataStart + 8;
                    bool hasbankBlock = IndexOf(fileContent, bankBlock, blockStart) != -1;

                    if (hasbankBlock)
                    {
                        byte[] bankData = new byte[fileSize + 8];
                        Array.Copy(fileContent, bankDataStart, bankData, 0, fileSize + 8);
                        bankDataList.Add(bankData);
                    }

                    bankDataStart += fileSize + 8;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"提取bank数据时出错: {ex.Message}");
                }
            }
            return bankDataList;
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

        private List<string> ExtractbanksFromFile(string filePath)
        {
            List<string> extractedFileNames = new List<string>();

            try
            {
                ExtractionProgress?.Invoke(this, $"正在处理文件: {Path.GetFileName(filePath)}");

                byte[] fileContent = File.ReadAllBytes(filePath);
                List<byte[]> bankDataList = ExtractbankData(fileContent);
                int count = 0;

                foreach (byte[] bankData in bankDataList)
                {
                    string baseFilename = Path.GetFileNameWithoutExtension(filePath);
                    string extractedFilename = $"{baseFilename}_{count}.bank";
                    string? dirName = Path.GetDirectoryName(filePath);
                    string extractedPath;

                    if (dirName != null)
                    {
                        extractedPath = Path.Combine(dirName, extractedFilename);
                    }
                    else
                    {
                        extractedPath = Path.Combine(Directory.GetCurrentDirectory(), extractedFilename);
                    }

                    string? dirToCreate = Path.GetDirectoryName(extractedPath);
                    if (dirToCreate != null && !Directory.Exists(dirToCreate))
                    {
                        Directory.CreateDirectory(dirToCreate);
                    }

                    File.WriteAllBytes(extractedPath, bankData);
                    ExtractionProgress?.Invoke(this, $"已提取: {extractedPath}");
                    extractedFileNames.Add(extractedPath);
                    count++;
                }
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"处理文件 {filePath} 时出错: {ex.Message}");
            }

            return extractedFileNames;
        }

        public override void Extract(string directoryPath)
        {
            List<string> allExtractedFileNames = new List<string>();

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"目录不存在: {directoryPath}");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始从目录 {directoryPath} 提取BANK文件");

            try
            {
                string[] files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                int totalFiles = files.Length;
                int processedFiles = 0;

                foreach (string filePath in files)
                {
                    if (Path.GetExtension(filePath).Equals(".bank", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    processedFiles++;
                    ExtractionProgress?.Invoke(this, $"处理进度: {processedFiles}/{totalFiles} - {Path.GetFileName(filePath)}");

                    List<string> fileNames = ExtractbanksFromFile(filePath);
                    allExtractedFileNames.AddRange(fileNames);
                }

                ExtractionCompleted?.Invoke(this, $"提取完成，共提取 {allExtractedFileNames.Count} 个BANK文件");
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"提取过程中发生错误: {ex.Message}");
            }

            FilesExtracted?.Invoke(this, allExtractedFileNames);
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            List<string> allExtractedFileNames = new List<string>();

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"目录不存在: {directoryPath}");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始从目录 {directoryPath} 提取BANK文件");

            try
            {
                string[] files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                TotalFilesToExtract = files.Length;
                int processedFiles = 0;

                foreach (string filePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Path.GetExtension(filePath).Equals(".bank", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    processedFiles++;
                    ExtractionProgress?.Invoke(this, $"处理进度: {processedFiles}/{TotalFilesToExtract} - {Path.GetFileName(filePath)}");

                    List<string> fileNames = await Task.Run(() => ExtractbanksFromFile(filePath), cancellationToken);
                    allExtractedFileNames.AddRange(fileNames);

                    OnProgressUpdated((processedFiles * 100) / TotalFilesToExtract);
                }

                ExtractionCompleted?.Invoke(this, $"提取完成，共提取 {allExtractedFileNames.Count} 个BANK文件");
            }
            catch (OperationCanceledException)
            {
                ExtractionError?.Invoke(this, "提取操作已取消");
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"提取过程中发生错误: {ex.Message}");
            }

            FilesExtracted?.Invoke(this, allExtractedFileNames);
        }
    }
}