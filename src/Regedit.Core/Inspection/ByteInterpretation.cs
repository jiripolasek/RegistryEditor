// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Inspection;

public enum ByteInterpretation
{
    Hex, Utf8, Utf16LittleEndian, Utf16BigEndian, Ascii, WindowsGuid, Rfc4122Guid, UnixSecondsInt64LittleEndian,
    UnixMillisecondsInt64LittleEndian, FileTimeLittleEndian
}
