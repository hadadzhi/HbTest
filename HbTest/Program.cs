using HbTest.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HbTest
{
    internal class Program
    {
        private const string FFmpegCmd = "ffmpeg.exe";
        private const double ImagesPerSecond = 1/3d;

        private const string InputPath = "input";
        private const string AudioPath = @"output\audio";
        private const string ImagesPath = @"output\images";

        private static void Main(string[] args)
        {
            if (!Directory.Exists(InputPath))
            {
                Logger.Error("Input directory does not exist");
                Environment.Exit(1);
            }

            Directory.CreateDirectory(AudioPath);
            Directory.CreateDirectory(ImagesPath);

            Logger.Info("Started");

            var taskList = new List<Task>();
            foreach (string file in Directory.EnumerateFiles(InputPath))
            {
                // I couldn't figure out another way to call async methods from main since main itself can't be async :(
                var t = Task.Run(async () =>
                {
                    var fileName = Path.GetFileName(file);

                    try
                    {
                        var ba = File.ReadAllBytes(file);

                        var t1 = ExtractAndSaveAudioAsync(new MemoryStream(ba), fileName);
                        var t2 = ExtractAndSaveFramesAsync(new MemoryStream(ba), fileName);

                        await t1;
                        await t2;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Processing {fileName} has thrown:\n{ex}");
                        Environment.ExitCode = 1;
                    }
                });
                taskList.Add(t);
            }

            Task.WaitAll(taskList.ToArray());
            Logger.Info("All done, exiting");
        }

        private static async Task ExtractAndSaveAudioAsync(Stream input, string inputName)
        {
            Logger.Info($"Extracting audio from {inputName}");

            var output = new MemoryStream();
            await RunFFmpegAsync("-vn -i - -f wav -bitexact -", input, output); // without -bitexact ffmpeg writes additional chunks in the WAV header which complicates things

            FixFFmpegWavOutput(output);

            output.Seek(0, SeekOrigin.Begin);

            Logger.Info($"Writing audio extracted from {inputName}");

            using (var fs = File.Open($@"{AudioPath}\{inputName}-audio.wav", FileMode.Create))
            {
                await output.CopyToAsync(fs);
            }
        }

        /// <summary>
        /// When writing to a pipe, ffmpeg can't seek back to the beginning of the stream
        /// to set size bits in the header, so we have to do it ourselves.
        /// ref: http://soundfile.sapp.org/doc/WaveFormat/
        /// </summary>
        private static void FixFFmpegWavOutput(MemoryStream output)
        {
            var chunkSize = (int) output.Length - 8;
            var csb = BitConverter.GetBytes(chunkSize);
            var b = output.GetBuffer();
            b[4] = csb[0];
            b[5] = csb[1];
            b[6] = csb[2];
            b[7] = csb[3];

            var subchunk2Size = (int) output.Length - 44;
            var scsb = BitConverter.GetBytes(subchunk2Size);
            b[40] = scsb[0];
            b[41] = scsb[1];
            b[42] = scsb[2];
            b[43] = scsb[3];
        }

        private static async Task RunFFmpegAsync(string args, Stream input, Stream output, string command = FFmpegCmd)
        {
            var info = new ProcessStartInfo
            {
                FileName = $@"ffmpeg\{command}",
                Arguments = args,
                CreateNoWindow = false,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            };
            using (var proc = Process.Start(info))
            {
                if (null == proc)
                {
                    throw new Exception("ffmpeg failed to start");
                }

                var tIn = input.CopyToAsync(proc.StandardInput.BaseStream);
                var tOut = proc.StandardOutput.BaseStream.CopyToAsync(output);

                var stdErr = new StringBuilder();

                proc.BeginErrorReadLine();
                proc.ErrorDataReceived += (sender, eventArgs) => stdErr.Append(eventArgs.Data);

                try
                {
                    await tIn;
                    proc.StandardInput.Close();
                }
                catch
                {
                    // Ignore stream errors: ffmpeg may exit before we finish writing to the stream,
                    // which is normal, and we'll see ffmpeg error output later
                }

                try
                {
                    await tOut;
                    proc.StandardOutput.Close();
                }
                catch
                {
                    // Ignore stream errors: we'll see ffmpeg error output later
                }

                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    throw new Exception($"ffmpeg exited with error code {proc.ExitCode}:\n{stdErr}");
                }
            }
        }

        private static async Task ExtractAndSaveFramesAsync(Stream input, string inputName)
        {
            Logger.Info($"Extracting frames from {inputName}");

            var ffmpegOutput = new MemoryStream();
            await RunFFmpegAsync($"-i - -vf fps=1/{(int) Math.Round(1 / ImagesPerSecond)} -c:v png -f image2pipe -", input, ffmpegOutput);

            ffmpegOutput.Seek(0, SeekOrigin.Begin);

            Logger.Info($"Parsing frames extracted from {inputName}");
            var images = ParseConcatenatedPNGs(ffmpegOutput);

            Logger.Info($"Writing frames extracted from {inputName}");
            var i = 0;
            foreach (var imageBytes in images)
            {
                File.WriteAllBytes($@"{ImagesPath}\{inputName}-image-{i++}.png", imageBytes);
            }
        }

        private static IEnumerable<byte[]> ParseConcatenatedPNGs(Stream stream)
        {
            var list = new List<byte[]>();
            byte[] pngBytes;
            while ((pngBytes = ReadOnePNG(stream)) != null)
            {
                list.Add(pngBytes);
            }
            return list;
        }

        private static readonly byte[] PngSignature = {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A};

        private static byte[] ReadOnePNG(Stream stream)
        {
            var signature = new byte[8];
            stream.Read(signature, 0, 8);
            if (!signature.SequenceEqual(PngSignature))
            {
                return null; // PNG signature not found, assume end of stream
            }

            var ms = new MemoryStream();

            ms.Write(signature, 0, signature.Length);

            string chunkType = null;
            while (chunkType != "IEND")
            {
                var length = CopyUInt32BE(stream, ms);
                chunkType = CopyChunkType(stream, ms);
                CopyChunk(length + 4, stream, ms, 4096); // length + 4 to copy CRC, too; bufsize == 4096 b/c typical chunk size for PNG
            }

            return ms.GetBuffer();
        }

        private static void CopyChunk(long length, Stream stream, Stream ms, int bufSize = 81920)
        {
            while (length > 0)
            {
                var b = new byte[Math.Min(length, bufSize)];
                var read = stream.Read(b, 0, b.Length);
                ms.Write(b, 0, b.Length);
                length -= read;
            }
        }

        private static uint CopyUInt32BE(Stream input, Stream output)
        {
            var b = new byte[4];
            
            input.Read(b, 0, 4);
            output.Write(b, 0, 4);
            
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b);
            }

            return BitConverter.ToUInt32(b, 0);
        }

        private static string CopyChunkType(Stream input, Stream output)
        {
            var b = new byte[4];
            
            input.Read(b, 0, 4);
            output.Write(b, 0, 4);

            return Encoding.ASCII.GetString(b, 0, 4);
        }
    }
}
