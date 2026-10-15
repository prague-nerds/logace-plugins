using System;

namespace LogAce.Plugins.Syslog;

/// <summary>Parsed RFC3164/RFC5424 message fields used by the stable spool schema.</summary>
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
