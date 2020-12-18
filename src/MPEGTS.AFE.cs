using System;
using System.Buffers;

namespace MediaStreams
{
    public partial class MPEGTS
    {
        public enum AdaptationFieldExtensionFlags
        {
            SeamlessSplice = 0x0020,
            PiecewiseRate = 0x0040,
            LegalTimeWindow = 0x0080
        }

        public struct AdaptationFieldExtension
        {
            public AdaptationFieldExtensionFlags Flags { get; set; }

            public short LegalTimeWindow { get; set; }

            public int PiecewiseRate { get; set; }

            public byte[] SeamlessSplice { get; set; }

            /// <summary>
            /// The length of this message in bytes
            /// </summary>
            /// <returns></returns>
            public int Length =>
                1 /* Length byte */ +
                1 /* Flags */ +
                (Flags.HasFlag(AdaptationFieldExtensionFlags.LegalTimeWindow) ? 2 : 0) +
                (Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate) ? 3 : 0) +
                (Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice) ? 5 : 0);

            public override string ToString()
            {
                return $@"Flags = {Flags}, LTW = {LegalTimeWindow}, PWR = {PiecewiseRate}, SS = {(SeamlessSplice != null ? BitConverter.ToString(SeamlessSplice).Replace("-", "") : null)}";
            }

            public static AdaptationFieldExtension Parse(ReadOnlySequence<byte> buf)
            {
                var sr = new SequenceReader<byte>(buf);
                if (sr.TryRead(out byte afeLen) && sr.TryRead(out byte afeFlags))
                {
                    var afe = new AdaptationFieldExtension()
                    {
                        Flags = (AdaptationFieldExtensionFlags)afeFlags
                    };

                    if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.LegalTimeWindow))
                    {
                        if (sr.TryReadLittleEndian(out short ltw))
                        {
                            afe.LegalTimeWindow = ltw;
                        }
                        else
                        {
                            return default;
                        }
                    }

                    if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate))
                    {
                        if (sr.TryRead(out byte pw0) && sr.TryRead(out byte pw1) && sr.TryRead(out byte pw2))
                        {
                            //TODO: test this
                            var pwv = pw0 << 16
                                & pw1 << 8
                                & pw2;
                            afe.PiecewiseRate = pwv;
                        }
                        else
                        {
                            return default;
                        }
                    }

                    if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice))
                    {
                        //TODO: parse this properly
                        var ss = new byte[5];
                        if (sr.TryCopyTo(ss))
                        {
                            afe.SeamlessSplice = ss;
                        }
                        else
                        {
                            return default;
                        }
                    }
                    return afe;
                }
                else
                {
                    return default;
                }
            }

            public bool Write(Span<byte> mem)
            {
                var len = Length;
                if (mem.Length < len)
                {
                    throw new Exception($"Not enough space to write {nameof(AdaptationFieldExtension)}, need {len} bytes, have {mem.Length} bytes.");
                }

                var span = mem.Slice(0, len);
                var offset = 0;

                span[offset++] = (byte)len;
                span[offset++] = (byte)Flags;

                if (Flags.HasFlag(AdaptationFieldExtensionFlags.LegalTimeWindow))
                {
                    if (!BitConverter.TryWriteBytes(span.Slice(offset), LegalTimeWindow))
                    {
                        return false;
                    }
                    offset += 2;
                }

                if (Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate))
                {
                    //TODO: write pwr
                    offset += 3;
                }

                if (Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice))
                {
                    SeamlessSplice.CopyTo(span.Slice(offset));
                    offset += 5;
                }

                return true;
            }
        }
    }
}