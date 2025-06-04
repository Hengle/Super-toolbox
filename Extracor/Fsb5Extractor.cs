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

        public event EventHandler<List<string>>? FilesExtracted;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Extract(directoryPath), cancellationToken);
        }

        public override void Extract(string directoryPath)
        {
            List<string> allExtractedFiles = new List<string>();
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
                bool hasArcFiles = Directory.EnumerateFiles(directoryPath, "*.arc", SearchOption.AllDirectories).Any();
                bool hasTabFiles = Directory.EnumerateFiles(directoryPath, "*.tab", SearchOption.AllDirectories).Any();

                if (hasArcFiles && hasTabFiles)
                {
                    outputQueue.Add("检测到ARC和TAB文件，使用正当防卫4提取逻辑...");
                    ProcessJustCause4ArcFiles(directoryPath, outputQueue, allExtractedFiles);
                }
                else
                {
                    outputQueue.Add("未检测到ARC和TAB文件，使用普通方法提取...");
                    ProcessNormalFiles(directoryPath, outputQueue, allExtractedFiles);
                }
            }
            catch (Exception ex)
            {
                outputQueue.Add($"处理目录时出错: {ex.Message}");
            }
            finally
            {
                outputQueue.CompleteAdding();
                outputThread.Join(1000);
                FilesExtracted?.Invoke(this, allExtractedFiles);
            }
        }

        private void ProcessJustCause4ArcFiles(string directoryPath, BlockingCollection<string> outputQueue, List<string> allExtractedFiles)
        {
            var arcFiles = Directory.EnumerateFiles(directoryPath, "*.arc", SearchOption.AllDirectories).ToList();
            outputQueue.Add($"找到 {arcFiles.Count} 个ARC文件");

            foreach (var arcFilePath in arcFiles)
            {
                try
                {
                    if (!HasFsbHeaderInSecondLine(arcFilePath))
                    {
                        outputQueue.Add($"跳过 {arcFilePath}：第二行未找到FSB5头");
                        continue;
                    }

                    var outputDir = Path.Combine(Path.GetDirectoryName(arcFilePath)!, "Extracted");
                    Directory.CreateDirectory(outputDir);
                    var baseName = Path.GetFileNameWithoutExtension(arcFilePath);

                    using var fs = new FileStream(arcFilePath, FileMode.Open, FileAccess.Read);
                    var content = new byte[fs.Length];
                    fs.Read(content, 0, (int)fs.Length);

                    var fsbPositions = FindFsbPositions(content);
                    if (!fsbPositions.Any())
                    {
                        outputQueue.Add($"在 {arcFilePath} 中未找到FSB5标记");
                        continue;
                    }

                    for (int i = 0; i < fsbPositions.Count; i++)
                    {
                        int startPos = fsbPositions[i];
                        int endPos = i < fsbPositions.Count - 1 ? fsbPositions[i + 1] : content.Length;

                        var extractedData = new byte[endPos - startPos];
                        Array.Copy(content, startPos, extractedData, 0, extractedData.Length);

                        var outputFilePath = Path.Combine(outputDir, $"{baseName}_{i}.fsb");
                        File.WriteAllBytes(outputFilePath, extractedData);

                        outputQueue.Add($"已提取: {outputFilePath} (位置: {startPos}-{endPos})");

                        ProcessExtractedFile(outputFilePath, outputQueue);

                        lock (allExtractedFiles)
                        {
                            allExtractedFiles.Add(outputFilePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    outputQueue.Add($"处理 {arcFilePath} 时出错: {ex.Message}");
                }
            }
        }

        private void ProcessNormalFiles(string directoryPath, BlockingCollection<string> outputQueue, List<string> allExtractedFiles)
        {
            var filesToProcess = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase))
                .ToList();

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
                        var nextPosition = FindFsb5Position(fileData, position + 4);
                        var fsbLength = (nextPosition > 0 ? nextPosition : fileData.Length) - position;

                        var fsbData = new byte[fsbLength];
                        Array.Copy(fileData, position, fsbData, 0, fsbLength);

                        var outputPath = Path.Combine(
                            Path.GetDirectoryName(filePath) ?? directoryPath,
                            $"{Path.GetFileNameWithoutExtension(filePath)}_{extractedCount}.fsb");

                        File.WriteAllBytes(outputPath, fsbData);

                        outputQueue.Add($"已提取: {outputPath}");
                        lock (allExtractedFiles)
                        {
                            allExtractedFiles.Add(outputPath);
                        }

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
                    outputQueue.Add($"处理 {filePath} 时出错: {ex.Message}");
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
                if (buffer[i] == FSB5_MAGIC[0] &&
                    buffer[i + 1] == FSB5_MAGIC[1] &&
                    buffer[i + 2] == FSB5_MAGIC[2] &&
                    buffer[i + 3] == FSB5_MAGIC[3] &&
                    buffer[i + 4] == FSB5_MAGIC[4] &&
                    buffer[i + 5] == FSB5_MAGIC[5] &&
                    buffer[i + 6] == FSB5_MAGIC[6] &&
                    buffer[i + 7] == FSB5_MAGIC[7])
                {
                    return true;
                }
            }

            return false;
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
                offset++;
            }

            return positions;
        }

        private int FindFsb5Position(byte[] data, int startIndex)
        {
            for (int i = startIndex; i <= data.Length - FSB5_MAGIC.Length; i++)
            {
                if (data[i] == FSB5_MAGIC[0] &&
                    data[i + 1] == FSB5_MAGIC[1] &&
                    data[i + 2] == FSB5_MAGIC[2] &&
                    data[i + 3] == FSB5_MAGIC[3] &&
                    data[i + 4] == FSB5_MAGIC[4] &&
                    data[i + 5] == FSB5_MAGIC[5] &&
                    data[i + 6] == FSB5_MAGIC[6] &&
                    data[i + 7] == FSB5_MAGIC[7])
                {
                    return i;
                }
            }

            return -1;
        }

        private void ProcessExtractedFile(string filePath, BlockingCollection<string> outputQueue)
        {
            try
            {
                var content = File.ReadAllBytes(filePath);

                if (content.Length > 16)
                {
                    content = content.Take(content.Length - 16).ToArray();
                }

                int endIndex = content.Length - 1;
                while (endIndex >= 0 && content[endIndex] == 0x30)
                {
                    endIndex--;
                }

                if (endIndex < content.Length - 1)
                {
                    content = content.Take(endIndex + 1).ToArray();
                }

                File.WriteAllBytes(filePath, content);
                outputQueue.Add($"已处理: {filePath}");
            }
            catch (Exception ex)
            {
                outputQueue.Add($"处理文件 {filePath} 时出错: {ex.Message}");
            }
        }
    }
}