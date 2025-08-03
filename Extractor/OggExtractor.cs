using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class OggExtractor : BaseExtractor
    {
        private bool _stopParsingOnFormatError = true;
        private int _globalIndex = 0;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(directoryPath))
                {
                    OnExtractionFailed($"输入的 {directoryPath} 不是一个有效的路径。");
                    return;
                }

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
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            ProcessFile(filePath, extractedDir);
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"处理文件 {filePath} 时出现错误: {ex.Message}");
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
            }, cancellationToken);
        }

        protected virtual void OnProgressUpdated()
        {
            Console.WriteLine($"已提取文件数: {ExtractedFileCount}/{TotalFilesToExtract}");
        }

        private void ProcessFile(string filePath, string outputDir)
        {
            long offset = 0;
            Dictionary<uint, FileStream> outputStreams = new Dictionary<uint, FileStream>();
            string fileNamePrefix = Path.GetFileNameWithoutExtension(filePath);

            using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read))
            {
                try
                {
                    while ((offset = ParseFile.GetNextOffset(fs, offset, XiphOrgOggContainer.MAGIC_BYTES)) > -1)
                    {
                        byte pageType = ParseFile.ParseSimpleOffset(fs, offset + 5, 1)[0];
                        uint bitstreamSerialNumber = BitConverter.ToUInt32(ParseFile.ParseSimpleOffset(fs, offset + 0xE, 4), 0);
                        byte segmentCount = ParseFile.ParseSimpleOffset(fs, offset + 0x1A, 1)[0];

                        uint sizeOfAllSegments = 0;
                        for (byte i = 0; i < segmentCount; i++)
                        {
                            sizeOfAllSegments += ParseFile.ParseSimpleOffset(fs, offset + 0x1B + i, 1)[0];
                        }

                        long pageSize = 0x1B + segmentCount + sizeOfAllSegments;
                        byte[] rawPageBytes = ParseFile.ParseSimpleOffset(fs, offset, (int)pageSize);
                        bool pageWrittenToFile = false;

                        if ((pageType & XiphOrgOggContainer.PAGE_TYPE_BEGIN_STREAM) == XiphOrgOggContainer.PAGE_TYPE_BEGIN_STREAM)
                        {
                            if (outputStreams.ContainsKey(bitstreamSerialNumber))
                            {
                                if (_stopParsingOnFormatError)
                                {
                                    throw new FormatException(
                                        $"多次找到流开始页面，但没有流结束页面，用于序列号: {bitstreamSerialNumber:X8}，文件: {filePath}");
                                }
                                else
                                {
                                    Console.WriteLine($"警告：对于文件 <{filePath}>，多次找到流开始页面但没有流结束页面，序列号为: {bitstreamSerialNumber:X8}。");
                                    continue;
                                }
                            }
                            else
                            {
                                string outputFileName = Path.Combine(outputDir, $"{fileNamePrefix}_{_globalIndex}.ogg");
                                outputFileName = GetNonDuplicateFileName(outputFileName);

                                outputStreams[bitstreamSerialNumber] = File.Open(outputFileName, FileMode.CreateNew, FileAccess.Write);
                                outputStreams[bitstreamSerialNumber].Write(rawPageBytes, 0, rawPageBytes.Length);
                                pageWrittenToFile = true;

                                OnFileExtracted(outputFileName);
                                _globalIndex++;
                            }
                        }

                        if (outputStreams.ContainsKey(bitstreamSerialNumber))
                        {
                            if (!pageWrittenToFile)
                            {
                                outputStreams[bitstreamSerialNumber].Write(rawPageBytes, 0, rawPageBytes.Length);
                                pageWrittenToFile = true;
                            }
                        }
                        else
                        {
                            if (_stopParsingOnFormatError)
                            {
                                throw new FormatException(
                                    $"找到没有流开始页的流数据页，用于序列号: {bitstreamSerialNumber:X8}，文件: {filePath}");
                            }
                            else
                            {
                                Console.WriteLine($"警告：对于文件 <{filePath}>，找到没有流开始页的流数据页，序列号为: {bitstreamSerialNumber:X8}。");
                                continue;
                            }
                        }

                        if ((pageType & XiphOrgOggContainer.PAGE_TYPE_END_STREAM) == XiphOrgOggContainer.PAGE_TYPE_END_STREAM)
                        {
                            if (outputStreams.ContainsKey(bitstreamSerialNumber))
                            {
                                if (!pageWrittenToFile)
                                {
                                    outputStreams[bitstreamSerialNumber].Write(rawPageBytes, 0, rawPageBytes.Length);
                                    pageWrittenToFile = true;
                                }

                                outputStreams[bitstreamSerialNumber].Close();
                                outputStreams[bitstreamSerialNumber].Dispose();
                                outputStreams.Remove(bitstreamSerialNumber);
                            }
                            else
                            {
                                if (_stopParsingOnFormatError)
                                {
                                    throw new FormatException(
                                        $"找到没有流开始页面的流结束页面，用于序列号: {bitstreamSerialNumber:X8}，文件: {filePath}");
                                }
                                else
                                {
                                    Console.WriteLine($"警告：对于文件 <{filePath}>，找到没有流开始页面的流结束页面，序列号为: {bitstreamSerialNumber:X8}。");
                                }
                            }
                        }

                        offset += pageSize;
                    }
                }
                catch (Exception ex)
                {
                    if (_stopParsingOnFormatError)
                    {
                        throw;
                    }
                    else
                    {
                        OnExtractionFailed($"处理文件 {filePath} 时出现异常: {ex.Message}");
                    }
                }
                finally
                {
                    foreach (uint k in outputStreams.Keys)
                    {
                        outputStreams[k].Close();
                        outputStreams[k].Dispose();
                    }
                }
            }
        }

        private string GetNonDuplicateFileName(string fileName)
        {
            string directory = Path.GetDirectoryName(fileName) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int counter = 1;
            string newFileName = fileName;

            while (File.Exists(newFileName))
            {
                newFileName = Path.Combine(directory, $"{name}_{counter}{extension}");
                counter++;
            }

            return newFileName;
        }

        private static class ParseFile
        {
            public static byte[] ParseSimpleOffset(FileStream fs, long offset, int length)
            {
                byte[] buffer = new byte[length];
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Read(buffer, 0, length);
                return buffer;
            }

            public static long GetNextOffset(FileStream fs, long offset, byte[] magicBytes)
            {
                byte[] buffer = new byte[magicBytes.Length];
                while (offset + magicBytes.Length <= fs.Length)
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Read(buffer, 0, magicBytes.Length);

                    if (AreByteArraysEqual(buffer, magicBytes))
                    {
                        return offset;
                    }

                    offset++;
                }

                return -1;
            }

            private static bool AreByteArraysEqual(byte[] a, byte[] b)
            {
                if (a.Length != b.Length) return false;

                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i]) return false;
                }

                return true;
            }
        }

        private static class XiphOrgOggContainer
        {
            public static readonly byte[] MAGIC_BYTES = { 0x4F, 0x67, 0x67, 0x53 };
            public const byte PAGE_TYPE_BEGIN_STREAM = 0x02;
            public const byte PAGE_TYPE_END_STREAM = 0x04;
        }
    }
}