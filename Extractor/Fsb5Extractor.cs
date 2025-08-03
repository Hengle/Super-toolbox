using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class Fsb5Extractor : BaseExtractor
    {
        private static readonly byte[] FSB5_MAGIC = { 0x46, 0x53, 0x42, 0x35, 0x01, 0x00, 0x00, 0x00 };
        private static readonly object consoleLock = new object();
        private const int BUFFER_SIZE = 1024 * 1024; // 1MB缓冲区
        private const byte JC4_PADDING_BYTE = 0x30; // 正当防卫4填充字节

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(directoryPath))
                throw new ArgumentException("目录路径不能为空", nameof(directoryPath));

            await Task.Run(() => Extract(directoryPath), cancellationToken);
        }

        public override void Extract(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath))
                throw new ArgumentException("目录路径不能为空", nameof(directoryPath));

            var outputQueue = new BlockingCollection<string>();

            var outputThread = new Thread(() =>
            {
                foreach (var message in outputQueue.GetConsumingEnumerable())
                {
                    lock (consoleLock)
                    {
                        Console.WriteLine(message);
                    }
                }
            })
            { IsBackground = true };
            outputThread.Start();

            try
            {
                var allFiles = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
                TotalFilesToExtract = allFiles.Count;

                bool hasArcFiles = allFiles.Any(f => f.EndsWith(".arc", StringComparison.OrdinalIgnoreCase));
                bool hasTabFiles = allFiles.Any(f => f.EndsWith(".tab", StringComparison.OrdinalIgnoreCase));
                bool hasResourceFiles = allFiles.Any(f => f.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));

                if (hasArcFiles && hasTabFiles)
                {
                    outputQueue.Add("检测到ARC和TAB文件，使用正当防卫4提取逻辑...");
                    ProcessJustCause4ArcFiles(allFiles, outputQueue);
                }
                else if (hasResourceFiles)
                {
                    outputQueue.Add("检测到RESOURCE文件，使用资源文件提取逻辑...");
                    ProcessResourceFiles(allFiles, outputQueue);
                }
                else
                {
                    outputQueue.Add("未检测到特殊文件类型，使用普通方法提取...");
                    ProcessNormalFiles(allFiles, outputQueue);
                }
            }
            catch (Exception ex)
            {
                outputQueue.Add($"处理目录时出错: {ex.Message}");
                OnExtractionFailed($"处理目录时出错: {ex.Message}");
            }
            finally
            {
                outputQueue.CompleteAdding();
                outputThread.Join(1000);
                OnExtractionCompleted();
            }
        }

        private void ProcessJustCause4ArcFiles(List<string> allFiles, BlockingCollection<string> outputQueue)
        {
            var arcFiles = allFiles.Where(f => f.EndsWith(".arc", StringComparison.OrdinalIgnoreCase)).ToList();
            outputQueue.Add($"找到 {arcFiles.Count} 个ARC文件");

            Parallel.ForEach(arcFiles, arcFilePath =>
            {
                try
                {
                    if (!HasFsbHeaderInSecondLine(arcFilePath))
                    {
                        outputQueue.Add($"跳过 {Path.GetFileName(arcFilePath)}：第二行未找到FSB5头");
                        return;
                    }

                    string outputDir = Path.Combine(Path.GetDirectoryName(arcFilePath) ?? Directory.GetCurrentDirectory(), "Extracted");
                    Directory.CreateDirectory(outputDir);
                    string baseName = Path.GetFileNameWithoutExtension(arcFilePath) ?? "unknown_arc";

                    byte[] content = File.ReadAllBytes(arcFilePath);
                    var fsbPositions = FindFsbPositions(content);

                    if (!fsbPositions.Any())
                    {
                        outputQueue.Add($"在 {Path.GetFileName(arcFilePath)} 中未找到FSB5标记");
                        return;
                    }

                    for (int i = 0; i < fsbPositions.Count; i++)
                    {
                        int startPos = fsbPositions[i];
                        int endPos = i < fsbPositions.Count - 1 ? fsbPositions[i + 1] : content.Length;

                        byte[] extractedData = new byte[endPos - startPos];
                        Array.Copy(content, startPos, extractedData, 0, extractedData.Length);

                        string outputPath = Path.Combine(outputDir, $"{baseName}_{i}.fsb");

                        File.WriteAllBytes(outputPath, extractedData);

                        CleanJustCause4FsbFile(outputPath, outputQueue);

                        OnFileExtracted(outputPath);
                    }
                }
                catch (Exception ex)
                {
                    outputQueue.Add($"处理 {Path.GetFileName(arcFilePath)} 时出错: {ex.Message}");
                    OnExtractionFailed($"处理 {Path.GetFileName(arcFilePath)} 时出错: {ex.Message}");
                }
            });
        }

        private void CleanJustCause4FsbFile(string filePath, BlockingCollection<string> outputQueue)
        {
            try
            {
                byte[] content = File.ReadAllBytes(filePath);
                int originalLength = content.Length;

                if (content.Length > 16)
                {
                    content = content.Take(content.Length - 16).ToArray();
                }

                int lastValidIndex = content.Length - 1;
                while (lastValidIndex >= 0 && content[lastValidIndex] == JC4_PADDING_BYTE)
                {
                    lastValidIndex--;
                }

                if (lastValidIndex < content.Length - 1)
                {
                    content = content.Take(lastValidIndex + 1).ToArray();
                }

                File.WriteAllBytes(filePath, content);

                outputQueue.Add($"已清理: {Path.GetFileName(filePath)} (原始大小: {originalLength} 字节, 清理后: {content.Length} 字节, 移除: {originalLength - content.Length} 字节)");
            }
            catch (Exception ex)
            {
                outputQueue.Add($"清理文件 {Path.GetFileName(filePath)} 时出错: {ex.Message}");
                OnExtractionFailed($"清理文件 {Path.GetFileName(filePath)} 时出错: {ex.Message}");
            }
        }

        private void ProcessResourceFiles(List<string> allFiles, BlockingCollection<string> outputQueue)
        {
            const int OVERLAP_SIZE = 32;

            var resourceFiles = allFiles.Where(f => f.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)).ToList();
            outputQueue.Add($"找到 {resourceFiles.Count} 个RESOURCE文件");

            Parallel.ForEach(resourceFiles, resourceFile =>
            {
                try
                {
                    var magicPositions = new List<long>();
                    var buffer = new byte[BUFFER_SIZE];
                    var prevBuffer = new byte[OVERLAP_SIZE];
                    long filePosition = 0;
                    bool hasMoreData = true;

                    using (var fs = new FileStream(resourceFile, FileMode.Open, FileAccess.Read))
                    {
                        while (hasMoreData)
                        {
                            int bytesRead = fs.Read(buffer, 0, BUFFER_SIZE);
                            if (bytesRead < BUFFER_SIZE) hasMoreData = false;

                            for (int i = 0; i <= bytesRead - FSB5_MAGIC.Length; i++)
                            {
                                if (CheckMagicMatch(buffer, i))
                                {
                                    magicPositions.Add(filePosition + i);
                                }
                            }

                            if (filePosition > 0)
                            {
                                var combinedBuffer = new byte[OVERLAP_SIZE * 2];
                                Buffer.BlockCopy(prevBuffer, 0, combinedBuffer, 0, OVERLAP_SIZE);
                                Buffer.BlockCopy(buffer, 0, combinedBuffer, OVERLAP_SIZE, OVERLAP_SIZE);

                                for (int i = 0; i <= OVERLAP_SIZE; i++)
                                {
                                    if (CheckMagicMatch(combinedBuffer, i))
                                    {
                                        long pos = filePosition - OVERLAP_SIZE + i;
                                        if (!magicPositions.Contains(pos))
                                            magicPositions.Add(pos);
                                    }
                                }
                            }

                            Buffer.BlockCopy(buffer, bytesRead - OVERLAP_SIZE, prevBuffer, 0, OVERLAP_SIZE);
                            filePosition += bytesRead;
                        }
                    }

                    if (magicPositions.Count == 0)
                    {
                        outputQueue.Add($"跳过 {Path.GetFileName(resourceFile)}：未找到FSB5标记");
                        return;
                    }

                    string resourceDir = Path.GetDirectoryName(resourceFile) ?? string.Empty;
                    string outputDir = Path.Combine(resourceDir, "Extracted");
                    Directory.CreateDirectory(outputDir);
                    string baseName = Path.GetFileNameWithoutExtension(resourceFile);

                    using (var fs = new FileStream(resourceFile, FileMode.Open))
                    {
                        for (int i = 0; i < magicPositions.Count; i++)
                        {
                            long startPos = magicPositions[i];
                            long endPos = (i < magicPositions.Count - 1) ? magicPositions[i + 1] : new FileInfo(resourceFile).Length;
                            long size = endPos - startPos;

                            fs.Position = startPos;
                            byte[] fsbData = new byte[size];
                            int totalRead = 0;

                            while (totalRead < size)
                            {
                                int bytesRead = fs.Read(fsbData, totalRead, (int)Math.Min(BUFFER_SIZE, size - totalRead));
                                if (bytesRead == 0) break;
                                totalRead += bytesRead;
                            }

                            string outputPath = Path.Combine(outputDir, $"{baseName}_{i}.fsb");
                            File.WriteAllBytes(outputPath, fsbData);

                            outputQueue.Add($"已提取: {Path.GetFileName(outputPath)} (大小: {fsbData.Length}字节)");
                            OnFileExtracted(outputPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    outputQueue.Add($"处理 {Path.GetFileName(resourceFile)} 时出错: {ex.Message}");
                    OnExtractionFailed($"处理 {Path.GetFileName(resourceFile)} 时出错: {ex.Message}");
                }
            });
        }

        private void ProcessNormalFiles(List<string> allFiles, BlockingCollection<string> outputQueue)
        {
            var filesToProcess = allFiles.Where(f => !f.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)).ToList();
            outputQueue.Add($"找到 {filesToProcess.Count} 个待处理文件");

            Parallel.ForEach(filesToProcess, filePath =>
            {
                try
                {
                    var extractedCount = 0;
                    var fileData = File.ReadAllBytes(filePath);
                    var position = 0;

                    while ((position = FindFsb5Position(fileData, position)) >= 0)
                    {
                        var nextPosition = FindFsb5Position(fileData, position + FSB5_MAGIC.Length);
                        var fsbLength = (nextPosition > 0 ? nextPosition : fileData.Length) - position;

                        var fsbData = new byte[fsbLength];
                        Array.Copy(fileData, position, fsbData, 0, fsbLength);

                        string fileDir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                        var outputPath = Path.Combine(
                            fileDir,
                            $"{Path.GetFileNameWithoutExtension(filePath)}_{extractedCount}.fsb");

                        File.WriteAllBytes(outputPath, fsbData);

                        outputQueue.Add($"已提取: {Path.GetFileName(outputPath)} (大小: {fsbData.Length} 字节)");
                        OnFileExtracted(outputPath);

                        extractedCount++;
                        position += fsbLength;
                    }

                    if (extractedCount > 0)
                    {
                        outputQueue.Add($"从 {Path.GetFileName(filePath)} 中提取了 {extractedCount} 个FSB文件");
                    }
                }
                catch (Exception ex)
                {
                    outputQueue.Add($"处理 {Path.GetFileName(filePath)} 时出错: {ex.Message}");
                    OnExtractionFailed($"处理 {Path.GetFileName(filePath)} 时出错: {ex.Message}");
                }
            });
        }

        private bool HasFsbHeaderInSecondLine(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var buffer = new byte[256];
            int bytesRead = fs.Read(buffer, 0, 256);

            for (int i = 16; i <= bytesRead - FSB5_MAGIC.Length; i++)
            {
                if (CheckMagicMatch(buffer, i))
                {
                    return true;
                }
            }
            return false;
        }

        private bool CheckMagicMatch(byte[] data, int startIndex)
        {
            for (int j = 0; j < FSB5_MAGIC.Length; j++)
            {
                if (data[startIndex + j] != FSB5_MAGIC[j])
                {
                    return false;
                }
            }
            return true;
        }

        private List<int> FindFsbPositions(byte[] content)
        {
            var positions = new List<int>();
            int offset = 0;

            while (true)
            {
                offset = FindFsb5Position(content, offset);
                if (offset == -1) break;

                positions.Add(offset);
                offset += FSB5_MAGIC.Length; 
            }

            return positions;
        }

        private int FindFsb5Position(byte[] data, int startIndex)
        {
            if (data == null || data.Length < FSB5_MAGIC.Length || startIndex < 0)
                return -1;

            for (int i = startIndex; i <= data.Length - FSB5_MAGIC.Length; i++)
            {
                if (CheckMagicMatch(data, i))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
