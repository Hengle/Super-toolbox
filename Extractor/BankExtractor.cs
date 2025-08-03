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

        private static List<byte[]> ExtractBankData(byte[] fileContent)
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
                    bool hasBankBlock = IndexOf(fileContent, bankBlock, blockStart) != -1;

                    if (hasBankBlock)
                    {
                        byte[] bankData = new byte[fileSize + 8];
                        Array.Copy(fileContent, bankDataStart, bankData, 0, fileSize + 8);
                        bankDataList.Add(bankData);
                    }

                    bankDataStart += fileSize + 8;
                }
                catch (Exception ex)
                {
                    throw new Exception($"提取bank数据时出错: {ex.Message}");
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

        private List<string> ExtractBanksFromFile(string filePath, string extractedDir)
        {
            List<string> extractedFileNames = new List<string>();

            try
            {
                byte[] fileContent = File.ReadAllBytes(filePath);
                List<byte[]> bankDataList = ExtractBankData(fileContent);
                int count = 0;

                foreach (byte[] bankData in bankDataList)
                {
                    string baseFilename = Path.GetFileNameWithoutExtension(filePath);
                    string extractedFilename = $"{baseFilename}_{count}.bank";
                    string extractedPath = Path.Combine(extractedDir, extractedFilename);

                    string? directory = Path.GetDirectoryName(extractedPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllBytes(extractedPath, bankData);
                    extractedFileNames.Add(extractedPath);
                    OnFileExtracted(extractedPath);
                    count++;
                }
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
            }

            return extractedFileNames;
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"目录不存在: {directoryPath}");
                return;
            }

            try
            {
                string extractedDir = Path.Combine(directoryPath, "Extracted");
                Directory.CreateDirectory(extractedDir);

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

                    await Task.Run(() => ExtractBanksFromFile(filePath, extractedDir), cancellationToken);
                    processedFiles++;
                }

                OnExtractionCompleted();
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("提取操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"提取过程中发生错误: {ex.Message}");
            }
        }

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }
    }
}