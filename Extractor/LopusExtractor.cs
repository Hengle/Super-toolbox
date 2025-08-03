using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class LopusExtractor : BaseExtractor
    {
        private static readonly byte[] OPUS_HEADER = { 0x4F, 0x50, 0x55, 0x53 };
        private static readonly byte[] LOPUS_HEADER = { 0x01, 0x00, 0x00, 0x80, 0x18, 0x00, 0x00, 0x00 };

        private readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>();
        private Thread? _outputThread;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Extract(directoryPath), cancellationToken);
        }

        public override void Extract(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"目录 {directoryPath} 不存在");
                return;
            }

            StartOutputThread();

            try
            {
                var allFiles = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals("Extracted", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                TotalFilesToExtract = allFiles.Count;
                LogMessage($"找到 {allFiles.Count} 个文件待处理");

                ProcessFiles(allFiles, directoryPath);
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"处理目录时出错: {ex.Message}");
            }
            finally
            {
                CompleteProcessing();
            }
        }

        private void ProcessFiles(List<string> files, string baseDir)
        {
            var outputDir = Path.Combine(baseDir, "Extracted");
            Directory.CreateDirectory(outputDir);

            Parallel.ForEach(files, file =>
            {
                try
                {
                    if (IsOpusFile(file))
                        ProcessOpusFile(file, outputDir);
                    else
                        ProcessOtherFile(file, outputDir);
                }
                catch (Exception ex)
                {
                    LogError($"处理文件 {file} 时出错: {ex.Message}");
                }
            });
        }

        private void ProcessOpusFile(string filePath, string outputDir)
        {
            var content = File.ReadAllBytes(filePath);

            if (!IsValidOpus(content))
            {
                LogMessage($"文件 {Path.GetFileName(filePath)} 不是有效的OPUS文件");
                return;
            }

            if (TryExtractLopusData(content, out var lopusData) && lopusData != null)
            {
                SaveLopusFile(lopusData, Path.GetFileNameWithoutExtension(filePath), outputDir);
            }
            else
            {
                LogMessage($"警告: {Path.GetFileName(filePath)} 未找到LOPUS头，跳过处理");
            }
        }

        private void ProcessOtherFile(string filePath, string outputDir)
        {
            var content = File.ReadAllBytes(filePath);
            var opusSegments = FindOpusSegments(content);

            if (!opusSegments.Any())
                return;

            var baseName = Path.GetFileNameWithoutExtension(filePath);
            LogMessage($"在 {baseName} 中发现 {opusSegments.Count} 个OPUS片段");

            for (int i = 0; i < opusSegments.Count; i++)
            {
                if (TryExtractLopusData(opusSegments[i], out var lopusData) && lopusData != null)
                {
                    SaveLopusFile(lopusData, $"{baseName}_{i}", outputDir);
                }
                else
                {
                    LogMessage($"警告: {baseName}_{i} 未找到LOPUS头，跳过处理");
                }
            }
        }

        private bool TryExtractLopusData(byte[] opusData, out byte[]? lopusData)
        {
            lopusData = null;
            int pos = FindHeaderPosition(opusData, LOPUS_HEADER, 0);

            if (pos == -1) return false;

            lopusData = new byte[opusData.Length - pos];
            Array.Copy(opusData, pos, lopusData, 0, lopusData.Length);
            return true;
        }

        private List<byte[]> FindOpusSegments(byte[] data)
        {
            var segments = new List<byte[]>();
            var positions = FindAllHeaderPositions(data, OPUS_HEADER);

            for (int i = 0; i < positions.Count; i++)
            {
                int start = positions[i];
                int end = (i < positions.Count - 1) ? positions[i + 1] : data.Length;

                var segment = new byte[end - start];
                Array.Copy(data, start, segment, 0, segment.Length);
                segments.Add(segment);
            }

            return segments;
        }

        private void SaveLopusFile(byte[] data, string baseName, string outputDir)
        {
            string path = Path.Combine(outputDir, $"{baseName}.lopus");
            File.WriteAllBytes(path, data);
            OnFileExtracted(path);
            LogMessage($"已提取: {Path.GetFileName(path)} (大小: {data.Length}字节)");
        }

        #region Helper Methods
        private bool IsOpusFile(string filePath) =>
            Path.GetExtension(filePath).Equals(".opus", StringComparison.OrdinalIgnoreCase);

        private bool IsValidOpus(byte[] data) =>
            data.Length >= OPUS_HEADER.Length && CheckHeader(data, 0, OPUS_HEADER);

        private List<int> FindAllHeaderPositions(byte[] data, byte[] header)
        {
            var positions = new List<int>();
            for (int offset = 0; ; offset += header.Length)
            {
                offset = FindHeaderPosition(data, header, offset);
                if (offset == -1) break;
                positions.Add(offset);
            }
            return positions;
        }

        private int FindHeaderPosition(byte[] data, byte[] header, int startIndex)
        {
            int endIndex = data.Length - header.Length;
            for (int i = startIndex; i <= endIndex; i++)
                if (CheckHeader(data, i, header))
                    return i;
            return -1;
        }

        private bool CheckHeader(byte[] data, int startIndex, byte[] header)
        {
            for (int j = 0; j < header.Length; j++)
                if (data[startIndex + j] != header[j])
                    return false;
            return true;
        }
        #endregion

        #region Output Management
        private void StartOutputThread()
        {
            _outputThread = new Thread(ProcessOutputQueue) { IsBackground = true };
            _outputThread.Start();
        }

        private void CompleteProcessing()
        {
            _outputQueue.CompleteAdding();
            _outputThread?.Join(1000);
            OnExtractionCompleted();
        }

        private void ProcessOutputQueue()
        {
            foreach (var message in _outputQueue.GetConsumingEnumerable())
                Console.WriteLine(message);
        }

        private void LogMessage(string message) => _outputQueue.Add(message);
        private void LogError(string message) => _outputQueue.Add(message);
        #endregion
    }
}
