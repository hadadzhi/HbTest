using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HbTest
{
    class Program
    {
        private const string FfmpegCmd = "ffmpeg.exe";
        private const string FfprobeCmd = "ffprobe.exe";
        private const double FrameDelay = 3; // seconds

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage:\n\nHbTest <accountName> <keyValue>\n");
                return;
            }

            var account = new CloudStorageAccount(new StorageCredentials(args[0], args[1]), true);
            var client = account.CreateCloudBlobClient();

            var vc = client.GetContainerReference("videos");
            if (!vc.Exists())
            {
                Console.WriteLine("ERROR: Container 'videos' does not exist");
                Environment.Exit(1);
            }

            var taskList = new List<Task>();
            var blob = vc.ListBlobs().First();
            if (blob is CloudBlob cb)
            {
                // I couldn't figure out another way to call async methods from main since main itself can't be async :(
                var t = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessBlobAsync(cb, client);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: processing {cb.Name} has thrown:\n{ex}");
                        Environment.ExitCode = 1;
                    }
                });
                taskList.Add(t);
            }
            else
            {
                Console.WriteLine($"WARN: skipping {blob} (not a CloudBlob)");
            }

            Task.WaitAll(taskList.ToArray());
        }

        private static async Task ProcessBlobAsync(CloudBlob blob, CloudBlobClient client)
        {
            if (blob.Properties.ContentType != "video/mp4")
            {
                Console.WriteLine($"WARN: skipping {blob.Name} (not an mp4 file)");
                return;
            }

            Console.WriteLine($"INFO: processing {blob.Name}: {blob.Uri}");

            var ba = new byte[blob.Properties.Length];
            await blob.DownloadToStreamAsync(new MemoryStream(ba));

            var t1 = ExtractAndUploadAudioAsync(new MemoryStream(ba), blob.Name, client);
            var t2 = ExtractAndUploadFramesAsync(new MemoryStream(ba), blob.Name, client);

            await t1;
            await t2;

            Console.WriteLine($"INFO: finished processing {blob.Name}");
        }

        private static async Task ExtractAndUploadAudioAsync(Stream input, string inputName, CloudBlobClient client)
        {
            Console.WriteLine($"INFO: extracting audio from {inputName}");

            var output = new MemoryStream();
            await RunFfmpegAsync("-vn -i - -f wav -bitexact -", input, output); // without -bitexact ffmpeg writes additional chunks in the WAV header which complicates things

            FixFfmpegWavOutput(output);

            output.Seek(0, SeekOrigin.Begin);

            // For testing
//            using (var fs = File.Open("test.wav", FileMode.Create))
//            {
//                output.CopyTo(fs);
//            }

            Console.WriteLine($"INFO: uploading audio extracted from {inputName}");
            await UploadBlob("audios", $"{inputName}-audio-ng42.wav", output, client);
        }

        private static async Task UploadBlob(string containerName, string blobName, Stream data, CloudBlobClient client)
        {
            var container = client.GetContainerReference(containerName);
            if (!container.Exists())
            {
                throw new Exception($"container '{containerName}' does not exist");
            }

            var blob = container.GetBlockBlobReference(blobName);

            await blob.UploadFromStreamAsync(data);
        }

        /// <summary>
        /// When writing to a pipe, ffmpeg can't seek back to the beginning of the stream
        /// to set size bits in the header, so we have to do it ourselves.
        /// ref: http://soundfile.sapp.org/doc/WaveFormat/
        /// </summary>
        private static void FixFfmpegWavOutput(MemoryStream output)
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

        private static async Task RunFfmpegAsync(string args, Stream input, Stream output, string command = FfmpegCmd)
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
                try { await tIn; } catch {/*Ignore stream errors: ffmpeg may exit before we finish writing to the stream, which is normal, and we'll see ffmpeg error output later*/}
                proc.StandardInput.Close();
                try { await tOut;} catch {/*Ignore stream errors: we'll see ffmpeg error output later*/}
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    throw new Exception($"ffmpeg error:\n{proc.StandardError.ReadToEnd()}");
                }
            }
        }

        private static async Task ExtractAndUploadFramesAsync(Stream input, string inputName, CloudBlobClient client)
        {
            Console.WriteLine($"INFO: extracting frames from {inputName}");

            var ffprobeOutput = new MemoryStream();
            await RunFfmpegAsync("-i - -show_entries format=duration -of csv=\"p=0\"", input, ffprobeOutput, FfprobeCmd);

            var durationStr = System.Text.Encoding.ASCII.GetString(ffprobeOutput.GetBuffer(), 0, (int) ffprobeOutput.Length);
            var duration = double.Parse(durationStr, CultureInfo.InvariantCulture);

            var ts = new List<Task>();
            var t = 0d;
            var i = 0;
            while (t < duration)
            {
                var output = new MemoryStream();
                
                input.Seek(0, SeekOrigin.Begin);
                await RunFfmpegAsync($"-i - -ss {t} -vframes 1 -c:v png -f image2pipe -", input, output);
                
                output.Seek(0, SeekOrigin.Begin);
                
                t += FrameDelay;

                // For testing
//                ts.Add(image.CopyToAsync(File.Open($"image-{i++}.png", FileMode.Create)));

                Console.WriteLine($"INFO: uploading frame {i} extracted from {inputName}");
                ts.Add(UploadBlob("frames", $"{inputName}-frame_{i++}-ng42.png", output, client));
            }
            Task.WaitAll(ts.ToArray());
        }
    }
}
