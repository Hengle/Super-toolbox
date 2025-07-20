using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class Bnsf_Extractor : BaseExtractor
    {
        private static readonly byte[] BNSF_HEADER = { 0x42, 0x4E, 0x53, 0x46 }; // 'BNSF'
        private static readonly byte[] SFMT_MARKER = { 0x73, 0x66, 0x6D, 0x74 };  // 'sfmt'
        private const int BUFFER_SIZE = 81920;
        private const int SFMT_OFFSET = 12; 
        private const int MIN_BNSF_SIZE = 16;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            string outputDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(outputDir);

            var tldatFiles = Directory.GetFiles(directoryPath, "*.TLDAT", SearchOption.AllDirectories);
            Console.WriteLine($"准备处理 {tldatFiles.Length} 个文件...");

            int totalExtracted = 0;
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            try
            {
                await Task.WhenAll(tldatFiles.Select(async filePath =>
                {
                    await semaphore.WaitAsync(cancellationToken);

                    try
                    {
                        int extracted = await Task.Run(() => ProcessFileByChunks(filePath, outputDir), cancellationToken);
                        Interlocked.Add(ref totalExtracted, extracted);
                        Console.WriteLine($"{Path.GetFileName(filePath)}: 提取{extracted}个");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{Path.GetFileName(filePath)} 处理失败: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("操作被用户取消");
            }

            Console.WriteLine($"\n完成! 共提取 {totalExtracted} 个有效BNSF文件");
        }

        private int ProcessFileByChunks(string filePath, string outputDir)
        {
            int count = 0;
            byte[] buffer = new byte[BUFFER_SIZE];
            byte[] overlapBuffer = new byte[BNSF_HEADER.Length - 1];
            int overlapSize = 0;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, FileOptions.SequentialScan))
            {
                int bytesRead;
                while ((bytesRead = fs.Read(buffer, overlapSize, BUFFER_SIZE - overlapSize)) > 0)
                {
                    int totalBytes = bytesRead + overlapSize;
                    for (int i = 0; i < totalBytes - 15; i++)
                    {
                        if (buffer[i] == BNSF_HEADER[0] &&
                            buffer[i + 1] == BNSF_HEADER[1] &&
                            buffer[i + 2] == BNSF_HEADER[2] &&
                            buffer[i + 3] == BNSF_HEADER[3])
                        {
                            if (i + SFMT_OFFSET + 4 <= totalBytes &&
                                buffer[i + SFMT_OFFSET] == SFMT_MARKER[0] &&
                                buffer[i + SFMT_OFFSET + 1] == SFMT_MARKER[1] &&
                                buffer[i + SFMT_OFFSET + 2] == SFMT_MARKER[2] &&
                                buffer[i + SFMT_OFFSET + 3] == SFMT_MARKER[3])
                            {
                                long startPos = fs.Position - totalBytes + i;
                                long endPos = FindNextHeader(fs, startPos + 4);

                                if (endPos - startPos >= MIN_BNSF_SIZE)
                                {
                                    SaveBNSFChunk(fs, outputDir, Path.GetFileNameWithoutExtension(filePath),
                                        count++, startPos, endPos);
                                    i = (int)(endPos - (fs.Position - totalBytes)) - 1; 
                                }
                            }
                        }
                    }

                    overlapSize = Math.Min(BNSF_HEADER.Length - 1, totalBytes);
                    Array.Copy(buffer, totalBytes - overlapSize, overlapBuffer, 0, overlapSize);
                    Array.Copy(overlapBuffer, buffer, overlapSize);
                }
            }
            return count;
        }

        private long FindNextHeader(FileStream fs, long startSearchPos)
        {
            byte[] searchBuffer = new byte[BUFFER_SIZE];
            fs.Seek(startSearchPos, SeekOrigin.Begin);

            while (fs.Position < fs.Length)
            {
                int bytesRead = fs.Read(searchBuffer, 0, BUFFER_SIZE);
                for (int i = 0; i < bytesRead - 3; i++)
                {
                    if (searchBuffer[i] == BNSF_HEADER[0] &&
                        searchBuffer[i + 1] == BNSF_HEADER[1] &&
                        searchBuffer[i + 2] == BNSF_HEADER[2] &&
                        searchBuffer[i + 3] == BNSF_HEADER[3])
                    {
                        return fs.Position - bytesRead + i;
                    }
                }
            }
            return fs.Length;
        }

        private void SaveBNSFChunk(FileStream sourceFs, string outputDir,
            string baseName, int index, long startPos, long endPos)
        {
            string outputPath = Path.Combine(outputDir, $"{baseName}_{index}.bnsf");

            try
            {
                using (var outputFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    sourceFs.Seek(startPos, SeekOrigin.Begin);
                    byte[] copyBuffer = new byte[8192];
                    long bytesRemaining = endPos - startPos;

                    while (bytesRemaining > 0)
                    {
                        int bytesToCopy = (int)Math.Min(copyBuffer.Length, bytesRemaining);
                        int bytesRead = sourceFs.Read(copyBuffer, 0, bytesToCopy);
                        outputFs.Write(copyBuffer, 0, bytesRead);
                        bytesRemaining -= bytesRead;
                    }
                }
                OnFileExtracted(outputPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存失败 {outputPath}: {ex.Message}");
            }
        }
    }
}
