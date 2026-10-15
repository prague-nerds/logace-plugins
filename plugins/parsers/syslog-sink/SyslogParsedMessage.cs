using System;

internal sealed record SyslogParsedMessage(
    string Format,
    string Priority,
    string RawTime,
    DateTimeOffset? Timestamp,
    int TimestampFractionalDigits,
    string Host,
    string App,
    string ProcId,
    string MsgId,
    string StructuredData,
    string Facility,
    string Severity,
    string Message);
