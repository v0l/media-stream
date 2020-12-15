using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading.Tasks;

namespace TSER
{
    class Program
    {
        static Task Main(string[] args)
        {
            var hc = new HttpClient();
            var fs = new FileStream("/Users/kieran/test.ts", FileMode.Open);

            var pipe = new Pipe();
            var reader = Task.Run(async () =>
            {
                var r = pipe.Reader;
                try
                {
                    await foreach (var pkt in MPEGTS.TryReadPackets(r))
                    {
                        //Console.WriteLine(pkt);
                    }
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
                    //var rsp = await hc.GetStreamAsync("http://10.100.0.18:9981/play/stream/channel/0fe23673f24e32ed72d3f1953eaf145f?title=2%20%3A%20RTÉ2");
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
    }
}
