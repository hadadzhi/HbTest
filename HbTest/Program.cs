using HbTest.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HbTest
{
    internal class Program
    {
        private const string FFmpegCmd = "ffmpeg.exe";
        private const string FFprobeCmd = "ffprobe.exe";
        private const double ImagesPerSecond = 1/3d;

        private const string InputPath = "input";
        private const string AudioPath = @"output\audio";
        private const string ImagesPath = @"output\images";

        private static void Main(string[] args)
        {
            if (!Directory.Exists(InputPath))
            {
                Logger.Error("input directory does not exist");
                Environment.Exit(1);
            }

            Directory.CreateDirectory(AudioPath);
            Directory.CreateDirectory(ImagesPath);

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
                        Logger.Error($"processing {fileName} has thrown:\n{ex}");
                        Environment.ExitCode = 1;
                    }
                });
                taskList.Add(t);
            }

            Task.WaitAll(taskList.ToArray());
            Logger.Info("all done, exiting");
        }

        private static async Task ExtractAndSaveAudioAsync(Stream input, string inputName)
        {
            Logger.Info($"extracting audio from {inputName}");

            var output = new MemoryStream();
            await RunFFmpegAsync("-vn -i - -f wav -bitexact -", input, output); // without -bitexact ffmpeg writes additional chunks in the WAV header which complicates things

            FixFFmpegWavOutput(output);

            output.Seek(0, SeekOrigin.Begin);

            Logger.Info($"extracted audio from {inputName}, writing");

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
            Logger.Info($"extracting frames from {inputName}");

            var ffprobeOutput = new MemoryStream();
            await RunFFmpegAsync("-i - -show_entries format=duration -of csv=\"p=0\"", input, ffprobeOutput, FFprobeCmd);

            var durationStr = Encoding.ASCII.GetString(ffprobeOutput.GetBuffer(), 0, (int) ffprobeOutput.Length);
            var duration = double.Parse(durationStr, CultureInfo.InvariantCulture);

            var ts = new List<Task>();
            var t = 0d;
            var i = 0;
            while (t < duration)
            {
                var output = new MemoryStream();
                
                input.Seek(0, SeekOrigin.Begin);
                await RunFFmpegAsync($"-i - -ss {t} -vframes 1 -c:v png -f image2pipe -", input, output);
                
                output.Seek(0, SeekOrigin.Begin);

                t += 1 / ImagesPerSecond;

                Logger.Info($"extracted frame {i} from {inputName}, writing...");

                var fileStream = File.Open($@"{ImagesPath}\{inputName}-image-{i++}.png", FileMode.Create);
                ts.Add(output.CopyToAsync(fileStream).ContinueWith(task => fileStream.Close()));
            }
            Task.WaitAll(ts.ToArray());
        }
    }
}
