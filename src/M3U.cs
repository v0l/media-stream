using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaStreams
{
    public class M3U 
    {
        public const string Header = "#EXTM3U";
        public const string TrackStart = "#EXTINF";

        public List<Entry> Entries { get; set; }
    }

    public class Track : Entry
    {
        public string Title { get; set; }
        public int Duration { get; set; }
        public List<KeyValuePair<string, string>> Properties { get; set; }
        public Uri Path { get; set; }

        public string Tag => M3U.TrackStart;

        /// <summary>
        /// Very dirty :) 
        /// </summary>
        /// <returns></returns>
        public string Value => $"{Duration}{(Properties?.Count > 0 ? $" {(string.Join(" ", Properties.Select(a => $"{a.Key}=\"{a.Value}\"")))}" : string.Empty)},{Title}{Environment.NewLine}{Path}";
    }

    public class Generic : Entry
    {
        public string Tag { get; set; }

        public string Value { get; set; } 
    }

    public class Header : Entry
    {
        public string Tag => M3U.Header;

        public string Value => null;
    }

    public interface Entry
    {
        string Tag { get; }

        string Value { get; }
    }

    public static class M3UReader
    {
        internal readonly struct TagLine
        {
            public TagLine(string tag, string val)
            {
                Tag = tag;
                Value = val;
            }

            public readonly string Tag;
            public readonly string Value;
        }

        internal readonly struct LineEntry
        {
            public LineEntry(ReadOnlySequence<byte> data, SequencePosition sp)
            {
                Data = data;
                LineEnd = sp;
            }

            public readonly ReadOnlySequence<byte> Data;
            public readonly SequencePosition LineEnd;
        }

        const byte CR = (byte)'\r';
        const byte LF = (byte)'\n';
        const byte TagStart = (byte)'#';
        const byte TagDelimiter = (byte)':';

        private static LineEntry? ReadLine(ReadOnlySequence<byte> seq)
        {
            var sp = seq.PositionOf(LF);
            if(sp != null) 
            {
                var line = seq.Slice(0, sp.Value);
                if(line.Length > 1) 
                {
                    //check for CR
                    var sr = new SequenceReader<byte>(line);
                    if(sr.TryPeek(line.Length - 1, out byte cr) && cr == CR) 
                    {
                        line = line.Slice(0, line.Length - 1);
                    }
                }
                return new LineEntry(line, seq.GetPosition(1, sp.Value));
            }
            return default;
        }

        private static TagLine ReadTagLine(ReadOnlySequence<byte> line)
        {
            var tagend = line.PositionOf(TagDelimiter);
            if(tagend != null) 
            {
                var tag = Encoding.UTF8.GetString(line.Slice(0, tagend.Value));
                var value = Encoding.UTF8.GetString(line.Slice(line.GetPosition(1, tagend.Value)));
                return new TagLine(tag, value);
            } 
            else 
            {
                var tag = Encoding.UTF8.GetString(line);
                return new TagLine(tag, null);
            }
        }

        private static KeyValuePair<string, string> SplitTags(string tag)
        {
            var ts = tag.Split('=');
            return new KeyValuePair<string, string>(ts[0], ts[1].Substring(1, ts[1].Length - 2));
        }

        private static Track ReadTrackEntry(TagLine tag, LineEntry link) 
        {
            var ret = new Track();

            var propsAndTitle = tag.Value.Split(',');
            if(propsAndTitle[0].Contains(' '))
            {
                var tagRx = new Regex("(?<tag>[\\w\\-]+)\\=[\\\"|\\'](?<value>.*?)[\\\"|\\']+");
                
                var propsAndDuration = propsAndTitle[0].Split(' ', 2);
                if(int.TryParse(propsAndDuration[0], out int duration))
                {
                    ret.Duration = duration;
                }
                var tm = tagRx.Matches(propsAndDuration[1]);
                ret.Properties = tm.Select(a => new KeyValuePair<string, string>(a.Groups[1].Value, a.Groups[2].Value)).ToList();
            }
            else if(int.TryParse(propsAndTitle[0], out int duration))
            {
                ret.Duration = duration;
            }
            ret.Title = propsAndTitle[1];
            ret.Path = new Uri(Encoding.UTF8.GetString(link.Data));

            return ret;
        }

        private static Entry ReadEntry(ReadOnlySequence<byte> buf, LineEntry line, out SequencePosition? entryEnd)
        {
            var sr = new SequenceReader<byte>(line.Data);
            if(sr.TryPeek(out byte vStart) && vStart == TagStart) 
            {
                var tagLine = ReadTagLine(line.Data);
                if(tagLine.Tag == M3U.Header) 
                {
                    entryEnd = line.LineEnd;
                    return new Header();
                } 
                else if(tagLine.Tag == M3U.TrackStart)
                {
                    var trackLine = ReadLine(buf.Slice(line.LineEnd));
                    if(trackLine != null)
                    {
                        entryEnd = trackLine.Value.LineEnd; //Set line end to next line after track url
                        return ReadTrackEntry(tagLine, trackLine.Value);
                    }
                } 
                else 
                {
                    entryEnd = line.LineEnd;
                    return new Generic 
                    {
                        Tag = tagLine.Tag,
                        Value = tagLine.Value
                    };
                }
            }
            entryEnd = null;
            return default;
        }

        public static async IAsyncEnumerable<Entry> ReadPlaylist(PipeReader r, [EnumeratorCancellation]CancellationToken cancellationToken = default)
        {
            while(!cancellationToken.IsCancellationRequested) 
            {
                var readTask = await r.ReadAsync(cancellationToken);

                var buf = readTask.Buffer;
                SequencePosition? sp = null;
                do
                {
                    var line = ReadLine(buf);
                    if(line.HasValue)
                    {
                        var entry = ReadEntry(buf, line.Value, out SequencePosition? entryEnd);
                        if(entry != default) 
                        {
                            yield return entry;
                        }
                        else 
                        {
                            //invalid line entry
                        }
                        sp = entryEnd ?? line.Value.LineEnd;
                        buf = buf.Slice(sp.Value);
                    } 
                    else 
                    {
                        //cant read a line
                        break;
                    }
                } while(sp != null);

                if(sp != null)
                {
                    r.AdvanceTo(sp.Value);
                }
                else 
                {
                    r.AdvanceTo(readTask.Buffer.Start);
                }
                
                if(readTask.IsCanceled || readTask.IsCompleted) 
                {
                    break;
                }
            }
            
        }
    }
}