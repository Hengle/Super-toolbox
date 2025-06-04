using super_toolbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class DatExtractor : BaseExtractor
    {
        private readonly Dictionary<byte[], string> _markerExtensions = new Dictionary<byte[], string>(new ByteArrayComparer())
        {
            { Encoding.ASCII.GetBytes("3LC"), "cl3" },
            { Encoding.ASCII.GetBytes("xet"), "tex" },
            { Encoding.ASCII.GetBytes("smc"), "cms" },
            { Encoding.ASCII.GetBytes("cdm"), "mdc" },
            { Encoding.ASCII.GetBytes("ldm"), "mdl" },
            { Encoding.ASCII.GetBytes("emf"), "fme" },
            { Encoding.ASCII.GetBytes("hsc"), "csh" },
            { Encoding.ASCII.GetBytes("kpm"), "mpk" },
            { Encoding.ASCII.GetBytes("pim"), "mip" },
            { Encoding.ASCII.GetBytes("dsc"), "csd" },
            { Encoding.ASCII.GetBytes("nibg"), "gbin" },
            { Encoding.ASCII.GetBytes("rtsg"), "gstr" },
            { Encoding.ASCII.GetBytes("qstm"), "mtsq" },
        };

        private class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[]? x, byte[]? y)
            {
                if (x == null || y == null) return x == y;
                if (x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i] != y[i]) return false;
                }
                return true;
            }

            public int GetHashCode(byte[] obj)
            {
                int hash = 17;
                foreach (byte b in obj)
                {
                    hash = hash * 31 + b.GetHashCode();
                }
                return hash;
            }
        }

        public event EventHandler<List<string>>? FilesExtracted;
        public event EventHandler<string>? ExtractionStarted;
        public event EventHandler<string>? ExtractionProgress;
        public event EventHandler<string>? ExtractionError;

        public new event EventHandler<int>? ExtractionCompleted;

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            List<string> allExtractedFileNames = new List<string>();

            if (!Directory.Exists(directoryPath))
            {
                ExtractionError?.Invoke(this, $"目录不存在: {directoryPath}");
                OnExtractionFailed($"目录不存在: {directoryPath}");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始从目录 {directoryPath} 提取DAT文件");

            try
            {
                var datFiles = Directory.GetFiles(directoryPath, "*.dat", SearchOption.AllDirectories);
                int totalFiles = datFiles.Length;
                int processedFiles = 0;

                if (totalFiles == 0)
                {
                    ExtractionError?.Invoke(this, "在指定目录中未找到.dat文件");
                    OnExtractionFailed("在指定目录中未找到.dat文件");
                    return;
                }

                string outputDir = Path.Combine(directoryPath, "Extracted");
                Directory.CreateDirectory(outputDir);

                foreach (var datFile in datFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    processedFiles++;
                    ExtractionProgress?.Invoke(this, $"处理进度: {processedFiles}/{totalFiles} - {Path.GetFileName(datFile)}");

                    try
                    {
                        var fileNames = await ExtractFileAsync(datFile, outputDir, cancellationToken);
                        allExtractedFileNames.AddRange(fileNames);
                        OnFileExtracted(datFile);
                    }
                    catch (Exception ex)
                    {
                        ExtractionError?.Invoke(this, $"处理文件 {datFile} 时出错: {ex.Message}");
                        OnExtractionFailed($"处理文件 {datFile} 时出错: {ex.Message}");
                    }
                }

                ExtractionProgress?.Invoke(this, "开始进行解压操作...");
                await DecompressExtractedFilesAsync(outputDir, cancellationToken);

                ExtractionCompleted?.Invoke(this, allExtractedFileNames.Count);
                OnExtractionCompleted();
                ExtractionProgress?.Invoke(this, $"提取完成，共提取 {allExtractedFileNames.Count} 个DAT文件");
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

            FilesExtracted?.Invoke(this, allExtractedFileNames);
        }

        private async Task<List<string>> ExtractFileAsync(string filePath, string outputDir, CancellationToken cancellationToken)
        {
            List<string> extractedFileNames = new List<string>();

            try
            {
                ExtractionProgress?.Invoke(this, $"正在处理文件: {Path.GetFileName(filePath)}");

                byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
                List<int> bilzPositions = FindBilzPositions(content);
                if (bilzPositions.Count == 0)
                {
                    ExtractionError?.Invoke(this, $"文件 {Path.GetFileName(filePath)} 中未找到BILZ标记");
                    return extractedFileNames;
                }

                string baseName = Path.GetFileNameWithoutExtension(filePath);

                for (int i = 0; i < bilzPositions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int currentPos = bilzPositions[i];
                    (string? extension, int markerLength) = GetFileExtension(content, currentPos);

                    if (extension == null)
                    {
                        ExtractionError?.Invoke(this, $"在位置 {currentPos} 无法识别文件格式，跳过");
                        continue;
                    }

                    int endPos = content.Length;
                    if (i < bilzPositions.Count - 1)
                    {
                        int nextPos = bilzPositions[i + 1];
                        int offset = markerLength == 4 ? 128 : 127;
                        endPos = nextPos - offset;
                    }

                    byte[] extractedData = new byte[endPos - currentPos];
                    Array.Copy(content, currentPos, extractedData, 0, extractedData.Length);

                    string outputFile = Path.Combine(outputDir, $"{baseName}_{i}.{extension}");
                    await File.WriteAllBytesAsync(outputFile, extractedData, cancellationToken);
                    ExtractionProgress?.Invoke(this, $"已提取: {outputFile} (位置: {currentPos}-{endPos}, 标记类型: {markerLength}字节)");
                    extractedFileNames.Add(outputFile);
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

        private List<int> FindBilzPositions(byte[] content)
        {
            List<int> positions = new List<int>();
            byte[] bilzMarker = Encoding.ASCII.GetBytes("BILZ");
            int offset = 0;

            while (offset < content.Length)
            {
                offset = IndexOf(content, bilzMarker, offset);
                if (offset == -1) break;
                positions.Add(offset);
                offset++;
            }

            return positions;
        }

        private int IndexOf(byte[] source, byte[] pattern, int startIndex)
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
                if (match) return i;
            }
            return -1;
        }

        private (string? extension, int markerLength) GetFileExtension(byte[] content, int bilzPosition)
        {
            if (bilzPosition < 128) return (null, 0);

            byte[] markerRegion = new byte[128];
            Array.Copy(content, bilzPosition - 128, markerRegion, 0, 128);

            for (int i = 0; i <= markerRegion.Length - 4; i++)
            {
                byte[] marker = new byte[4];
                Array.Copy(markerRegion, i, marker, 0, 4);

                if (_markerExtensions.TryGetValue(marker, out string? ext))
                {
                    return (ext, 4);
                }
            }

            for (int i = 0; i <= markerRegion.Length - 3; i++)
            {
                byte[] marker = new byte[3];
                Array.Copy(markerRegion, i, marker, 0, 3);

                if (_markerExtensions.TryGetValue(marker, out string? ext))
                {
                    return (ext, 3);
                }
            }

            return (null, 0);
        }

        private async Task DecompressExtractedFilesAsync(string extractedDir, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(extractedDir)) return;

            string[] files = Directory.GetFiles(extractedDir, "*.*", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await DecompressFileAsync(file, cancellationToken);
                }
                catch (Exception ex)
                {
                    ExtractionError?.Invoke(this, $"解压文件 {Path.GetFileName(file)} 时出错: {ex.Message}");
                }
            }
        }

        private async Task DecompressFileAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
                int startIndex = IndexOf(content, new byte[] { 0x78, 0xDA }, 0);

                if (startIndex == -1)
                {
                    ExtractionError?.Invoke(this, $"在文件 {filePath} 中未找到 78 DA 标记，跳过");
                    return;
                }

                byte[] dataToDecompress = new byte[content.Length - startIndex];
                Array.Copy(content, startIndex, dataToDecompress, 0, dataToDecompress.Length);

                using MemoryStream inputStream = new MemoryStream(dataToDecompress);
                using System.IO.Compression.DeflateStream deflateStream = new System.IO.Compression.DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
                using MemoryStream outputStream = new MemoryStream();

                await deflateStream.CopyToAsync(outputStream, cancellationToken);
                byte[] decompressedData = outputStream.ToArray();

                await File.WriteAllBytesAsync(filePath, decompressedData, cancellationToken);
                ExtractionProgress?.Invoke(this, $"已解压并覆盖: {filePath}");
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"解压失败: {ex.Message}");
            }
        }
    }
}