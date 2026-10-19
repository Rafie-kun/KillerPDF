// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Text;

    /// <summary>
    /// The basic class for all JBIG2 segments.
    /// </summary>
    internal sealed class SegmentHeader
    {
        private static ISegmentData CreateSegmentData(int segmentType)
        {
            return segmentType switch
            {
                0 => new SymbolDictionary(),
                4 or 6 or 7 => new TextRegion(),
                16 => new PatternDictionary(),
                20 or 22 or 23 => new HalftoneRegion(),
                36 or 38 or 39 => new GenericRegion(),
                40 or 42 or 43 => new GenericRefinementRegion(),
                48 => new PageInformation(),
                50 => new EndOfStripe(),
                52 => new Profiles(),
                53 => new Table(),
                _ => throw new InvalidOperationException($"No segment class for type {segmentType}."),
            };
        }

        private readonly SubInputStream subInputStream;

        private byte pageAssociationFieldSize;

        private WeakReference<ISegmentData> segmentData;

        public SegmentHeader[] RtSegments { get; private set; }

        public int SegmentNumber { get; private set; }

        public int SegmentType { get; private set; }

        public int PageAssociation { get; private set; }

        public long SegmentHeaderLength { get; private set; }

        public long SegmentDataLength { get; private set; }

        public long SegmentDataStartOffset { get; set; }

        public bool IsRetained { get; private set; }

        public SegmentHeader(Jbig2Document document, SubInputStream sis, long offset, int organisationType)
        {
            subInputStream = sis;
            Parse(document, sis, offset, organisationType);
        }

        private void Parse(Jbig2Document document, SubInputStream subInputStr, long offset, int organisationType)
        {
            subInputStr.Seek(offset);

            // 7.2.2 Segment number
            ReadSegmentNumber(subInputStr);

            // 7.2.3 Segment header flags
            ReadSegmentHeaderFlag(subInputStr);

            // 7.2.4 Amount of referred-to segments
            int countOfReferredToSegments = ReadAmountOfReferredToSegments(subInputStr);

            // 7.2.5 Referred-to segments numbers
            int[] referredToSegmentNumbers = ReadReferredToSegmentsNumbers(subInputStr, countOfReferredToSegments);

            // 7.2.6 Segment page association (Checks how big the page association field is.)
            ReadSegmentPageAssociation(document, subInputStr, countOfReferredToSegments, referredToSegmentNumbers);

            // 7.2.7 Segment data length (Contains the length of the data part (in bytes).)
            ReadSegmentDataLength(subInputStr);

            ReadDataStartOffset(subInputStr, organisationType);
            ReadSegmentHeaderLength(subInputStr, offset);
        }

        /// <summary>
        /// 7.2.2 Segment number
        /// </summary>
        private void ReadSegmentNumber(SubInputStream subInputStr)
        {
            SegmentNumber = (int)(subInputStr.ReadBits(32) & 0xffffffff);
        }

        /// <summary>
        /// 7.2.3 Segment header flags
        /// </summary>
        private void ReadSegmentHeaderFlag(SubInputStream subInputStr)
        {
            // Bit 7: Retain Flag, if 1, this segment is flagged as retained;
            IsRetained = subInputStr.ReadBit() == 1;

            // Bit 6: Size of the page association field. One byte if 0, four bytes if 1;
            pageAssociationFieldSize = (byte)subInputStr.ReadBit();

            // Bit 5-0: Contains the values (between 0 and 62 with gaps) for segment types, specified in 7.3
            SegmentType = (int)(subInputStr.ReadBits(6) & 0xff);
        }

        /// <summary>
        /// 7.2.4 Amount of referred-to segments
        /// </summary>
        private static int ReadAmountOfReferredToSegments(SubInputStream subInputStr)
        {
            int countOfRTS = (int)(subInputStr.ReadBits(3) & 0xf);

            if (countOfRTS <= 4)
            {
                // Short format
                subInputStr.ReadBits(5);
            }
            else
            {
                if (countOfRTS != 7)
                    throw new Jbig2Exception("The JBIG2 referred segment count uses a reserved format.");
                // Long format
                countOfRTS = (int)(subInputStr.ReadBits(29) & 0xffffffff);
                int retentionBytes = (countOfRTS + 8) >> 3;
                RequireRemaining(subInputStr, retentionBytes);
                subInputStr.SkipBytes(retentionBytes);
            }
            return countOfRTS;
        }

        /// <summary>
        /// 7.2.5 Referred-to segments numbers
        /// Gathers all segment numbers of referred-to segments.The segments itself are stored in the
        /// <see cref="RtSegments"/> array.
        /// </summary>
        /// <param name="subInputStr">Wrapped source data input stream.</param>
        /// <param name="countOfReferredToSegments">The number of referred - to segments.</param>
        /// <returns>An array with the segment number of all referred - to segments.</returns>
        private int[] ReadReferredToSegmentsNumbers(SubInputStream subInputStr, int countOfReferredToSegments)
        {
            int rtsSize = (uint)SegmentNumber > 65536 ? 4 : (uint)SegmentNumber > 256 ? 2 : 1;
            RequireRemaining(subInputStr, (long)countOfReferredToSegments * rtsSize
                + (pageAssociationFieldSize == 0 ? 1 : 4) + 4);
            Jbig2Bitmap.ReserveAuxiliary(64L + (long)countOfReferredToSegments * (sizeof(int) + IntPtr.Size));
            int[] result = new int[countOfReferredToSegments];

            if (countOfReferredToSegments > 0)
            {
                RtSegments = new SegmentHeader[countOfReferredToSegments];

                for (int i = 0; i < countOfReferredToSegments; i++)
                {
                    result[i] = (int)(subInputStr.ReadBits(rtsSize << 3) & 0xffffffff);
                }
            }

            return result;
        }

        private static void RequireRemaining(SubInputStream input, long bytes)
        {
            if (bytes > input.Length - input.Position)
                throw new Jbig2Exception("The JBIG2 segment header is truncated.");
        }

        /// <summary>
        /// 7.2.6 Segment page association
        /// </summary>
        private void ReadSegmentPageAssociation(Jbig2Document document, SubInputStream subInputStr,
                int countOfReferredToSegments, int[] referredToSegmentNumbers)
        {
            if (pageAssociationFieldSize == 0)
            {
                // Short format
                PageAssociation = (short)(subInputStr.ReadBits(8) & 0xff);
            }
            else
            {
                // Long format
                PageAssociation = (int)(subInputStr.ReadBits(32) & 0xffffffff);
            }

            if (countOfReferredToSegments > 0)
            {
                Jbig2Page page = document.GetPage(PageAssociation);
                for (int i = 0; i < countOfReferredToSegments; i++)
                {
                    RtSegments[i] = null != page ? page.GetSegment(referredToSegmentNumbers[i])
                            : document.GetGlobalSegment(referredToSegmentNumbers[i]);
                }
            }
        }

        /// <summary>
        /// 7.2.7 Segment data length. Reads the length of the data part in bytes.
        /// </summary>
        private void ReadSegmentDataLength(SubInputStream subInputStr)
        {
            SegmentDataLength = subInputStr.ReadBits(32) & 0xffffffff;
        }

        /// <summary>
        /// Sets the offset only if organization type is SEQUENTIAL. If random, data starts after segment headers and can be
        /// determined when all segment headers are parsed and allocated.
        /// </summary>
        private void ReadDataStartOffset(SubInputStream subInputStr, int organisationType)
        {
            if (organisationType == Jbig2Document.SEQUENTIAL)
            {
                SegmentDataStartOffset = subInputStr.Position;
            }
        }

        private void ReadSegmentHeaderLength(SubInputStream subInputStr, long offset)
        {
            SegmentHeaderLength = subInputStr.Position - offset;
        }

        /// <summary>
        /// Creates and returns a new <see cref="SubInputStream"/> that provides the data part of this segment.
        /// It is a clipped view of the source input stream.
        /// </summary>
        /// <returns>The <see cref="SubInputStream"/> that represents the data part of the segment.</returns>
        public SubInputStream GetDataInputStream()
        {
            return new SubInputStream(subInputStream, SegmentDataStartOffset, SegmentDataLength);
        }

        /// <summary>
        /// Retrieves the segments' data part.
        /// </summary>
        public ISegmentData GetSegmentData()
        {
            ISegmentData segmentDataPart = null;

            segmentData?.TryGetTarget(out segmentDataPart);

            if (segmentDataPart is null)
            {
                try
                {
                    segmentDataPart = CreateSegmentData(SegmentType);
                    segmentDataPart.Init(this, GetDataInputStream());

                    segmentData = new WeakReference<ISegmentData>(segmentDataPart);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"Can't instantiate segment class for type {SegmentType}. {e.Message}", e);
                }
            }

            return segmentDataPart;
        }

        public void CleanSegmentData()
        {
            segmentData = null;
        }

        public override string ToString()
        {
            StringBuilder stringBuilder = new();

            if (RtSegments != null)
            {
                foreach (SegmentHeader s in RtSegments)
                {
                    stringBuilder.Append(s.SegmentNumber + " ");
                }
            }
            else
            {
                stringBuilder.Append("none");
            }

            return "\n#SegmentNr: " + SegmentNumber //
                    + "\n SegmentType: " + SegmentType //
                    + "\n PageAssociation: " + PageAssociation //
                    + "\n Referred-to segments: " + stringBuilder.ToString() //
                    + "\n"; //
        }
    }
}
