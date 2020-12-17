using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaStreams;
using Xunit;

namespace test
{
    public class MPEGTS_Tests
    {
        public Task PipeStream(Stream fs, Func<PipeReader, Task> handle)
        {
            var pipe = new Pipe();
            var reader = Task.Run(async () =>
            {
                var r = pipe.Reader;
                try
                {
                    await handle(r);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
                finally
                {
                    await r.CompleteAsync();
                }
            });
            var writer = Task.Run(async () =>
            {
                var w = pipe.Writer;
                try
                {
                    while (true)
                    {
                        var mem = w.GetMemory();
                        var rlen = await fs.ReadAsync(mem);
                        if (rlen == 0)
                        {
                            break;
                        }
                        w.Advance(rlen);
                        var wres = await w.FlushAsync();
                        if (wres.IsCompleted)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
                await w.CompleteAsync();
            });

            return Task.WhenAll(reader, writer);
        }

        public async Task CompareFiles(string a, string b)
        {
            using var af = new FileStream(a, FileMode.Open);
            using var bf = new FileStream(b, FileMode.Open);

            Assert.Equal(af.Length, bf.Length);

            using var sha = new SHA256Managed();
            var ha = BitConverter.ToString(await sha.ComputeHashAsync(af));
            var hb = BitConverter.ToString(await sha.ComputeHashAsync(bf));

            Assert.True(ha == hb);
        }

        [Fact]
        /// <summary>
        /// Read/Write a file and check the contents are identical
        /// </summary>
        /// <returns></returns>
        public async Task ReadWrite()
        {
            const string fpath = @"C:\Users\Kieran\Downloads\astra192E-ts1080-2018-05-11.ts";
            var fpath_tst = Path.ChangeExtension(fpath, ".ts.test");

            using var fsin = new FileStream(fpath, FileMode.Open, FileAccess.Read);
            using var fsout = new FileStream(fpath_tst, FileMode.Create);
            var mem = MemoryPool<byte>.Shared.Rent(MediaStreams.MPEGTS.PacketLength);
            await PipeStream(fsin, async (pr) =>
            {
                await foreach (var pkt in MediaStreams.MPEGTS.TryReadPackets(pr))
                {
                    //pkt.Write(mem.Memory.Span);
                    //await fsout.WriteAsync(mem.Memory.Slice(0, MediaStreams.MPEGTS.PacketLength));
                }
            });
            fsin.Close();
            fsout.Close();

            //compare the files
            await CompareFiles(fpath, fpath_tst);
        }

        [Fact]
        public async Task ReadM3U()
        {
            const string fpath = @"C:\Users\Kieran\Downloads\tv_channels_kieran@harkin.me_plus.m3u";

            var hc = new HttpClient();
            hc.Timeout = TimeSpan.FromSeconds(2);
            hc.DefaultRequestHeaders.Add("user-agent", "libmpv");

            using var fsin = new FileStream(fpath, FileMode.Open, FileAccess.Read);
            using var fsout = new FileStream(Path.ChangeExtension(fpath, ".checked.m3u8"), FileMode.Create);
            await PipeStream(fsin, async (pr) =>
            {
                await foreach (var entry in MediaStreams.M3UReader.ReadPlaylist(pr))
                {
                    if (entry is Track t)
                    {
                        try
                        {
                            var rsp = await hc.GetAsync(t.Path, HttpCompletionOption.ResponseHeadersRead);
                            if (!rsp.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"DOWN >> {t.Title}");
                                continue;
                            }
                        }
                        catch
                        {
                            Console.WriteLine($"DOWN >> {t.Title}");
                            continue;
                        }
                    }

                    Console.WriteLine($"{entry.Tag}:{entry.Value}");
                    var data = Encoding.UTF8.GetBytes(entry.Value != null ? $"{entry.Tag}:{entry.Value}" : entry.Tag);
                    await fsout.WriteAsync(data);
                    await fsout.WriteAsync(new byte[] { 13, 10 });
                    await fsout.FlushAsync();
                }
            });
            fsin.Close();
            fsout.Close();
        }
    }
}
