using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class WaveExtractor : BaseExtractor
    {
        private static readonly byte[] riffHeader = { 0x52, 0x49, 0x46, 0x46 };
        private static readonly byte[] audioBlock = { 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74 };

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(Path.Combine(directoryPath, "Extracted"), StringComparison.OrdinalIgnoreCase))
                .ToList();

            TotalFilesToExtract = files.Count;
            Console.WriteLine($"目录中的源文件数量为: {TotalFilesToExtract}");

            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            try
            {
                foreach (string filePath in files)
                {
                    ThrowIfCancellationRequested(cancellationToken);

                    try
                    {
                        var result = await ExtractFromFileAsync(filePath, extractedDir, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                    }
                }

                OnExtractionCompleted();
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("提取操作已被取消");
                throw;
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"提取过程中发生错误: {ex.Message}");
                throw;
            }
            int actualExtractedFileCount = Directory.EnumerateFiles(extractedDir, "*", SearchOption.AllDirectories).Count();
            Console.WriteLine($"统计提取文件数量: {ExtractedFileCount}，实际在Extracted文件夹中的子文件数量为: {actualExtractedFileCount}");

            if (ExtractedFileCount != actualExtractedFileCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private async Task<(List<string> ExtractedFileNames, int FileCount)> ExtractFromFileAsync(
            string filePath, string extractedDir, CancellationToken cancellationToken)
        {
            List<string> extractedFileNames = new List<string>();
            byte[] fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
            int fileCount = 0;

            string baseFilename = Path.GetFileNameWithoutExtension(filePath);

            foreach (byte[] waveData in ExtractWaveData(fileContent))
            {
                ThrowIfCancellationRequested(cancellationToken);

                string tempExtractedFilename = $"{baseFilename}_{fileCount}.temp";
                string tempExtractedPath = Path.Combine(extractedDir, tempExtractedFilename);

                await File.WriteAllBytesAsync(tempExtractedPath, waveData, cancellationToken);

                string detectedExtension = await AnalyzeAudioFormatAsync(tempExtractedPath, cancellationToken);
                string finalExtractedFilename = $"{baseFilename}_{fileCount}.{detectedExtension}";
                string finalExtractedPath = Path.Combine(extractedDir, finalExtractedFilename);

                File.Move(tempExtractedPath, finalExtractedPath);
                extractedFileNames.Add(finalExtractedPath);
                OnFileExtracted(finalExtractedPath);

                Console.WriteLine($"提取的文件: {finalExtractedPath}");
                fileCount++;
            }

            return (extractedFileNames, fileCount);
        }

        private static IEnumerable<byte[]> ExtractWaveData(byte[] fileContent)
        {
            int waveDataStart = 0;
            while ((waveDataStart = IndexOf(fileContent, riffHeader, waveDataStart)) != -1)
            {
                if (waveDataStart + 12 > fileContent.Length)
                {
                    Console.WriteLine($"警告: 在位置 {waveDataStart} 发现RIFF头，但文件剩余长度不足");
                    waveDataStart += 4;
                    continue;
                }

                int fileSize = BitConverter.ToInt32(fileContent, waveDataStart + 4);
                fileSize = (fileSize + 1) & ~1;

                if (fileSize <= 0 || waveDataStart + 8 + fileSize > fileContent.Length)
                {
                    Console.WriteLine($"警告: 在位置 {waveDataStart} 的RIFF头大小无效或超出文件范围");
                    waveDataStart += 4;
                    continue;
                }

                int blockStart = waveDataStart + 8;
                bool hasAudioBlock = IndexOf(fileContent, audioBlock, blockStart) != -1;

                if (hasAudioBlock)
                {
                    int actualLength = Math.Min(fileSize + 8, fileContent.Length - waveDataStart);
                    byte[] waveData = new byte[actualLength];
                    Array.Copy(fileContent, waveDataStart, waveData, 0, actualLength);
                    yield return waveData;
                }

                waveDataStart += Math.Max(4, fileSize + 8);
            }
        }

        private static int IndexOf(byte[] source, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
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
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private static async Task<string> AnalyzeAudioFormatAsync(string filePath, CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{filePath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.Start();

                await process.WaitForExitAsync(cancellationToken);
                string output = await process.StandardError.ReadToEndAsync();

                if (output.Contains("atrac3", StringComparison.OrdinalIgnoreCase))
                {
                    return "at3";
                }
                else if (output.Contains("atrac9", StringComparison.OrdinalIgnoreCase))
                {
                    return "at9";
                }
                else if (output.Contains("xma2", StringComparison.OrdinalIgnoreCase))
                {
                    return "xma";
                }
                else if (output.Contains("none", StringComparison.OrdinalIgnoreCase))
                {
                    return "wem";
                }
                else if (output.Contains("pcm_s8", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("pcm_s16le", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("pcm_s16be", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("pcm_s24le", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("pcm_s24be", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("pcm_s32le", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("pcm_s32be", StringComparison.OrdinalIgnoreCase))
                {
                    return "wav";
                }

                return "wav";
            }
        }
    }
}