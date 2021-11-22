using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MediaStreams 
{
    /// <summary>
    /// VCL NAL units are specified as those NAL units having nal_unit_type equal to 1 to 5, inclusive. All remaining NAL units are called non-VCL NAL units. 
    /// </summary>
    public enum NALUnitType 
    {
        /// <summary>
        /// Coded slice of a non-IDR picture 
        /// slice_layer_without_partitioning_rbsp( )
        /// </summary>
        CodedSliceNonIDR = 1,

        /// <summary>
        /// Coded slice data partition A
        /// slice_data_partition_a_layer_rbsp( )
        /// </summary>
        CodedSlicePartitionA = 2,

        /// <summary>
        /// Coded slice data partition B 
        /// slice_data_partition_b_layer_rbsp( )
        /// </summary>
        CodedSlicePartitionB = 3,

        /// <summary>
        /// Coded slice data partition C
        /// slice_data_partition_c_layer_rbsp( )
        /// </summary>
        CodedSlicePartitionC = 4,

        /// <summary>
        /// Coded slice of an IDR picture
        /// slice_layer_without_partitioning_rbsp( )
        /// </summary>
        CodedSliceIDR = 5,

        /// <summary>
        /// Supplemental enhancement information (SEI)
        /// sei_rbsp( )
        /// </summary>
        SEI = 6,

        /// <summary>
        /// Sequence parameter set
        /// seq_parameter_set_rbsp( )
        /// </summary>
        SeqParameterSet = 7,

        /// <summary>
        /// Picture parameter set 
        /// pic_parameter_set_rbsp( )
        /// </summary>
        PicParameterSet = 8,

        /// <summary>
        /// Access unit delimiter 
        /// access_unit_delimiter_rbsp( )
        /// </summary>       
        AccessUnitDelimiter = 9,

        /// <summary>
        /// End of sequence
        /// end_of_seq_rbsp( ) 
        /// </summary>
        EndOfSeq = 10,

        /// <summary>
        /// End of stream
        /// end_of_stream_rbsp( )
        /// </summary>
        EndOfStream = 11,

        /// <summary>
        /// Filler data
        /// filler_data_rbsp( )
        /// </summary>
        FillerData = 12
    }

    public interface RBSP 
    {
        SequencePosition Read(ReadOnlySequence<byte> v);
    }

    public static class RBSPUtil
    {
        /// <summary>
        /// Parse Exp-Golomb numbers
        /// </summary>
        /// <param name="sr">The data sequence to read from</param>
        /// <param name="bitoffset">Offset into the current byte</param>
        /// <returns>Offset is the number of bits read from the current byte of the SequenceReader</returns>
        public static (int? Value, int offset) ExpGolomb(ref SequenceReader<byte> sr, int bitoffset = 0) 
        {
            if (sr.TryPeek(out byte b0))
            {
                byte b = 0;
                int zeroBits = -1;
                for (b = 0; b == 0; zeroBits++)
                {
                    b = (byte)(b0 & (0x80 >> bitoffset));
                    bitoffset++;
                    if(bitoffset > 7) 
                    {
                        //move to next byte
                        sr.Advance(1);
                        bitoffset = 0;
                        if(!sr.TryPeek(out b0)) 
                        {
                            return (null, 0);
                        }
                    }
                }

                int ret = (int)Math.Pow(2, zeroBits) - 1;
                int adder = 0;

                //if enough bits in this byte
                var skipBits = 8 - bitoffset - zeroBits;
                if(skipBits > 0) 
                {
                    adder = b0 & ((int)(Math.Pow(2, zeroBits) - 1) << skipBits);
                    adder = adder >> skipBits;
                    bitoffset += zeroBits;
                    return (ret + adder, bitoffset);
                } 
                else 
                {
                    sr.Advance(1);
                    if(sr.TryPeek(out byte b1)) 
                    {
                        var remBits = 8 - bitoffset;
                        var nextBits = zeroBits - remBits;
                        var skipNext = 8 - nextBits;
                        adder |= (b0 & (int)Math.Pow(2, remBits) - 1) << nextBits;
                        adder |= b1 >> skipNext;
                        
                        bitoffset = nextBits;
                        return (ret + adder, bitoffset);
                    }
                }
                
                return (null, bitoffset);
            }
            return (null, bitoffset);
        }
    }

    public struct NAL
    {
        /// <summary>
        /// nal_ref_idc not equal to 0 specifies that the content of the NAL unit contains a sequence parameter set or a picture
        /// parameter set or a slice of a reference picture or a slice data partition of a reference picture.
        /// nal_ref_idc equal to 0 for a NAL unit containing a slice or slice data partition indicates that the slice or slice data
        /// partition is part of a non-reference picture.
        /// nal_ref_idc shall not be equal to 0 for sequence parameter set or picture parameter set NAL units. When nal_ref_idc is
        /// equal to 0 for one slice or slice data partition NAL unit of a particular picture, it shall be equal to 0 for all slice and slice
        /// data partition NAL units of the picture.
        /// nal_ref_idc shall not be equal to 0 for IDR NAL units, i.e., NAL units with nal_unit_type equal to 5.
        /// nal_ref_idc shall be equal to 0 for all NAL units having nal_unit_type equal to 6, 9, 10, 11, or 12. 
        /// </summary>
        public byte RefIDC { get; set; }

        /// <summary>
        /// specifies the type of RBSP data structure contained in the NAL unit as specified in Table 7-1. VCL NAL
        /// units are specified as those NAL units having nal_unit_type equal to 1 to 5, inclusive. All remaining NAL units are called
        /// non-VCL NAL units
        /// </summary>
        public NALUnitType Type { get; set; }

        public RBSP Payload { get; set; }
    }

    public static class NALReader 
    {
        private static (SequencePosition pos, NAL unit) ReadOne(ReadOnlySequence<byte> buf) 
        {
            var sr = new SequenceReader<byte>(buf);
            if(sr.TryRead(out byte hdr)) 
            {
                var fzb = hdr & 0x80;
                var refIdc = (hdr & 0x60) >> 5;
                var unitType = hdr & 0x1f;
                if(fzb != 0) 
                {
                    //throw new Exception("Invalid NAL unit");
                }

                var nal = new NAL
                {
                    RefIDC = (byte)refIdc,
                    Type = (NALUnitType)unitType
                };

                //TODO: move sr nBytes for unitType

                // if(sr.Remaining >= 3) 
                // {
                //     if(sr.TryRead(out byte b0) &&
                //         sr.TryRead(out byte b1) &&
                //         sr.TryRead(out byte b2) &&
                //         b0 == 0x00 && b1 == 0x00 && b1 == 0x03) {
                //             //emulation prevention
                //         }
                //     sr.Advance(-3);
                // }
                return (sr.Position, nal);
            }
            return (sr.Position, default);
        }

        public static async IAsyncEnumerable<NAL> ReadUnits(PipeReader pr, [EnumeratorCancellation] CancellationToken cancellationToken = default) 
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var data = await pr.ReadAsync(cancellationToken);
                var buf = data.Buffer;

                var sp = buf.Start;
                while (true)
                {
                    var (pos, pkt) = ReadOne(buf.Slice(sp));
                    if(sp.Equals(pos)) {
                        break;
                    }
                    sp = pos;
                    yield return pkt;
                }

                pr.AdvanceTo(sp);

                //no more data coming 
                if (data.IsCompleted)
                {
                    break;
                }
            }
        }
    }
}