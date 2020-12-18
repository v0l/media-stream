using System;
using System.Buffers;

namespace MediaStreams
{
    public partial class MPEGTS
    {
        public struct AdaptationField
        {
            public AdaptationFieldFlags Flags { get; set; }

            public long PCR { get; set; }

            public long OPCR { get; set; }

            public sbyte SpliceCountdown { get; set; }

            public byte[] TransportPrivateData { get; set; }

            public AdaptationFieldExtension AdaptationFieldExtension { get; set; }

            public byte Length { get; set; }

            public override string ToString()
            {
                return $@"
Flags                 : {Flags}
PCR                   : {PCR}
OPCR                  : {OPCR}
SpliceCountdown       : {SpliceCountdown}
TransportPrivateData  : {TransportPrivateData?.Length}
Extension             : {AdaptationFieldExtension}";
            }

            public void Validate()
            {
                if ((int)Flags < 0 || Flags > AdaptationFieldFlags.All)
                {
                    throw new Exception("Invalid AFF");
                }
                if (Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData) && TransportPrivateData == null)
                {
                    throw new Exception("TPD flag was set but was not loaded");
                }
            }

            /// <summary>
            /// The length of this message in bytes
            /// </summary>
            public int CalculatedLength =>
                1 /* Flags */ +
                (Flags.HasFlag(AdaptationFieldFlags.PCR) ? 6 : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.OPCR) ? 6 : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.SplicingPoint) ? 1 : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData) ? 1 + (TransportPrivateData?.Length ?? 0) : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension) ? 1 + AdaptationFieldExtension.Length : 0);

            private static long? ReadPTS(ReadOnlySequence<byte> buf)
            {
                var sr = new SequenceReader<byte>(buf);

                Span<byte> tmpNum = stackalloc byte[6];
                if (sr.TryCopyTo(tmpNum))
                {
                    long pcrBase = (long)tmpNum[0] << 25
                        | (long)tmpNum[1] << 17
                        | (long)tmpNum[2] << 9
                        | (long)tmpNum[3] << 1
                        | (long)(tmpNum[4] & 0x80) >> 7;
                    long ext = (long)(tmpNum[4] & 0x01) << 8
                        | (long)tmpNum[5];

                    return (pcrBase * 300) + ext;
                }
                return null;
            }

            /// <summary>
            /// Read the adaption field for this packet
            /// <para><see cref="af"/> must start at the begingging of the Adaption Field</para>
            /// </summary>
            /// <param name="af"></param>
            /// <returns>True if parsing was a success</returns>
            public static AdaptationField Parse(ReadOnlySequence<byte> af)
            {
                var sr = new SequenceReader<byte>(af);
                if (sr.TryRead(out byte adaptionFieldLength))
                {
                    var ret = new AdaptationField()
                    {
                        Length = adaptionFieldLength
                    };

                    if (ret.Length > 0 && sr.TryRead(out byte afFlags))
                    {
                        ret.Flags = (AdaptationFieldFlags)afFlags;

                        if (ret.Flags.HasFlag(AdaptationFieldFlags.PCR))
                        {
                            var pcr = ReadPTS(af.Slice(sr.Position));
                            if (pcr != null)
                            {
                                ret.PCR = pcr.Value;
                                sr.Advance(6);
                            }
                            else
                            {
                                return default;
                            }
                        }

                        if (ret.Flags.HasFlag(AdaptationFieldFlags.OPCR))
                        {
                            var opcr = ReadPTS(af.Slice(sr.Position));
                            if (opcr != null)
                            {
                                ret.OPCR = opcr.Value;
                                sr.Advance(6);
                            }
                            else
                            {
                                return default;
                            }
                        }

                        if (ret.Flags.HasFlag(AdaptationFieldFlags.SplicingPoint))
                        {
                            if (sr.TryRead(out byte sp))
                            {
                                ret.SpliceCountdown = (sbyte)sp;
                            }
                            else
                            {
                                return default;
                            }
                        }

                        if (ret.Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData))
                        {
                            if (sr.TryRead(out byte tpdLen))
                            {
                                var buf = new byte[tpdLen];
                                if (sr.TryCopyTo(buf))
                                {
                                    ret.TransportPrivateData = buf;
                                }
                                else
                                {
                                    return default;
                                }
                            }
                            else
                            {
                                return default;
                            }
                        }

                        if (ret.Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension))
                        {
                            var afe = AdaptationFieldExtension.Parse(af.Slice(sr.Position));
                            if (!afe.Equals(default))
                            {
                                ret.AdaptationFieldExtension = afe;
                            }
                        }
                    }
                    return ret;
                }
                return default;
            }

            public bool Write(Span<byte> mem)
            {
                var len = Length;
                if (mem.Length < Length + 1)
                {
                    throw new Exception($"Not enough space to write {nameof(AdaptationField)}, need {len} bytes, have {mem.Length} bytes.");
                }

                var span = mem.Slice(0, len + 1);

                span[0] = (byte)len;
                if (len > 0)
                {
                    span[1] = (byte)Flags;

                    int offset = 2;
                    if (Flags.HasFlag(AdaptationFieldFlags.PCR))
                    {
                        //TODO: write PCR
                        offset += 5;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.OPCR))
                    {
                        //TODO: write OPCR
                        offset += 5;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.SplicingPoint))
                    {
                        span[offset] = (byte)SpliceCountdown;
                        offset++;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData))
                    {
                        span[offset] = (byte)TransportPrivateData.Length;
                        offset++;
                        TransportPrivateData.CopyTo(span.Slice(offset));
                        offset += TransportPrivateData.Length;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension))
                    {
                        AdaptationFieldExtension.Write(span.Slice(offset));
                        offset += AdaptationFieldExtension.Length;
                    }

                    //fill remaining with stuffing
                    if (offset < Length - 1)
                    {
                        span.Slice(offset).Fill(0xff);
                    }
                }
                return true;
            }
        }
    }
}