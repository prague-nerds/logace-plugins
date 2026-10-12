using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;

namespace LogAce.Plugins.Otel;

/// <summary>Converts OTLP JSON/protobuf batches to the built-in parser schema.</summary>
internal static class OtelLogBatchNormalizer
{
    private static readonly DateTimeOffset UnixEpoch = DateTimeOffset.UnixEpoch;

    public static bool TryNormalizeJson(
        byte[] payload,
        Action<string> appendLine,
        out int recordCount,
        out string? error)
    {
        recordCount = 0;
        error = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("resourceLogs", out JsonElement resourceLogs)
                || resourceLogs.ValueKind != JsonValueKind.Array)
            {
                error = "OTLP JSON request must contain a resourceLogs array.";
                return false;
            }

            foreach (JsonElement resourceLogsElement in resourceLogs.EnumerateArray())
            {
                Dictionary<string, string> resourceAttributes = [];

                if (resourceLogsElement.TryGetProperty("resource", out JsonElement resource))
                {
                    resourceAttributes = ParseJsonAttributes(resource);
                }

                if (!resourceLogsElement.TryGetProperty("scopeLogs", out JsonElement scopeLogs)
                    || scopeLogs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement scopeLogsElement in scopeLogs.EnumerateArray())
                {
                    ParseJsonScope(scopeLogsElement, out string scopeName, out string scopeVersion);

                    if (!scopeLogsElement.TryGetProperty("logRecords", out JsonElement logRecords)
                        || logRecords.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement logRecord in logRecords.EnumerateArray())
                    {
                        NormalizedOtelLogRecord normalized = ParseJsonLogRecord(logRecord, resourceAttributes, scopeName, scopeVersion);
                        appendLine(Serialize(normalized));
                        recordCount++;
                    }
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"OTLP JSON request is invalid: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Could not normalize OTLP JSON logs: {ex.Message}";
            return false;
        }
    }

    public static bool TryNormalizeProtobuf(
        byte[] payload,
        Action<string> appendLine,
        out int recordCount,
        out string? error)
    {
        recordCount = 0;
        error = null;

        try
        {
            var reader = new ProtoReader(payload);

            while (reader.TryReadField(out ProtoField field))
            {
                if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
                {
                    ParseResourceLogs(field.Bytes, appendLine, ref recordCount);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not normalize OTLP protobuf logs: {ex.Message}";
            return false;
        }
    }

    private static NormalizedOtelLogRecord ParseJsonLogRecord(
        JsonElement logRecord,
        IReadOnlyDictionary<string, string> resourceAttributes,
        string scopeName,
        string scopeVersion)
    {
        string severityNumber = GetJsonScalar(logRecord, "severityNumber");
        string severityText = GetJsonScalar(logRecord, "severityText");
        string severity = string.IsNullOrWhiteSpace(severityText)
            ? FormatSeverityNumber(severityNumber)
            : severityText;

        Dictionary<string, string> attributes = ParseJsonAttributes(logRecord);
        string time = FormatUnixNano(GetJsonScalar(logRecord, "timeUnixNano"));
        string observedAt = FormatUnixNano(GetJsonScalar(logRecord, "observedTimeUnixNano"));
        string traceId = NormalizeHexId(GetJsonScalar(logRecord, "traceId"));
        string spanId = NormalizeHexId(GetJsonScalar(logRecord, "spanId"));
        string eventName = GetJsonScalar(logRecord, "eventName");
        string body = logRecord.TryGetProperty("body", out JsonElement bodyElement)
            ? FormatJsonAnyValue(bodyElement)
            : string.Empty;

        return new NormalizedOtelLogRecord(
            Time: time,
            ObservedAt: observedAt,
            Severity: severity,
            SeverityNumber: severityNumber,
            ServiceName: resourceAttributes.GetValueOrDefault("service.name") ?? string.Empty,
            ScopeName: scopeName,
            ScopeVersion: scopeVersion,
            TraceId: traceId,
            SpanId: spanId,
            EventName: eventName,
            Attributes: FormatAttributeMap(attributes),
            Resource: FormatAttributeMap(resourceAttributes),
            Body: body);
    }

    private static Dictionary<string, string> ParseJsonAttributes(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!owner.TryGetProperty("attributes", out JsonElement attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement attribute in attributes.EnumerateArray())
        {
            if (attribute.ValueKind != JsonValueKind.Object
                || !attribute.TryGetProperty("key", out JsonElement keyElement))
            {
                continue;
            }

            string key = keyElement.GetString() ?? string.Empty;

            if (key.Length == 0)
            {
                continue;
            }

            string value = attribute.TryGetProperty("value", out JsonElement valueElement)
                ? FormatJsonAnyValue(valueElement)
                : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static void ParseJsonScope(JsonElement scopeLogsElement, out string scopeName, out string scopeVersion)
    {
        scopeName = string.Empty;
        scopeVersion = string.Empty;

        if (!scopeLogsElement.TryGetProperty("scope", out JsonElement scope)
            || scope.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        scopeName = GetJsonScalar(scope, "name");
        scopeVersion = GetJsonScalar(scope, "version");
    }

    private static string GetJsonScalar(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static string FormatJsonAnyValue(JsonElement valueElement)
    {
        if (valueElement.ValueKind != JsonValueKind.Object)
        {
            return GetJsonScalarValue(valueElement);
        }

        if (valueElement.TryGetProperty("stringValue", out JsonElement stringValue))
        {
            return stringValue.GetString() ?? string.Empty;
        }

        if (valueElement.TryGetProperty("boolValue", out JsonElement boolValue))
        {
            return boolValue.ValueKind == JsonValueKind.True ? "true" : "false";
        }

        if (valueElement.TryGetProperty("intValue", out JsonElement intValue))
        {
            return GetJsonScalarValue(intValue);
        }

        if (valueElement.TryGetProperty("doubleValue", out JsonElement doubleValue))
        {
            return GetJsonScalarValue(doubleValue);
        }

        if (valueElement.TryGetProperty("bytesValue", out JsonElement bytesValue))
        {
            return bytesValue.GetString() ?? string.Empty;
        }

        if (valueElement.TryGetProperty("arrayValue", out JsonElement arrayValue)
            && arrayValue.TryGetProperty("values", out JsonElement values)
            && values.ValueKind == JsonValueKind.Array)
        {
            return FormatJsonArray(values);
        }

        if (valueElement.TryGetProperty("kvlistValue", out JsonElement kvlistValue)
            && kvlistValue.TryGetProperty("values", out JsonElement keyValues)
            && keyValues.ValueKind == JsonValueKind.Array)
        {
            return FormatAttributeMap(ParseJsonKeyValueArray(keyValues));
        }

        return valueElement.GetRawText();
    }

    private static string GetJsonScalarValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static string FormatJsonArray(JsonElement values)
    {
        var items = new List<string>();

        foreach (JsonElement item in values.EnumerateArray())
        {
            items.Add(FormatJsonAnyValue(item));
        }

        return "[" + string.Join(", ", items) + "]";
    }

    private static Dictionary<string, string> ParseJsonKeyValueArray(JsonElement keyValues)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (JsonElement item in keyValues.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("key", out JsonElement keyElement))
            {
                continue;
            }

            string key = keyElement.GetString() ?? string.Empty;

            if (key.Length == 0)
            {
                continue;
            }

            result[key] = item.TryGetProperty("value", out JsonElement valueElement)
                ? FormatJsonAnyValue(valueElement)
                : string.Empty;
        }

        return result;
    }

    private static void ParseResourceLogs(ReadOnlySpan<byte> data, Action<string> appendLine, ref int recordCount)
    {
        var resourceAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopeLogsPayloads = new List<byte[]>();
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                resourceAttributes = ParseResource(field.Bytes);
            }
            else if (field.Number == 2 && field.WireType == ProtoWireType.LengthDelimited)
            {
                scopeLogsPayloads.Add(field.Bytes.ToArray());
            }
        }

        foreach (byte[] scopeLogsPayload in scopeLogsPayloads)
        {
            ParseScopeLogs(scopeLogsPayload, resourceAttributes, appendLine, ref recordCount);
        }
    }

    private static Dictionary<string, string> ParseResource(ReadOnlySpan<byte> data)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                ParseKeyValue(field.Bytes, attributes);
            }
        }

        return attributes;
    }

    private static void ParseScopeLogs(
        ReadOnlySpan<byte> data,
        IReadOnlyDictionary<string, string> resourceAttributes,
        Action<string> appendLine,
        ref int recordCount)
    {
        string scopeName = string.Empty;
        string scopeVersion = string.Empty;
        var logRecordPayloads = new List<byte[]>();
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                ParseScope(field.Bytes, out scopeName, out scopeVersion);
            }
            else if (field.Number == 2 && field.WireType == ProtoWireType.LengthDelimited)
            {
                logRecordPayloads.Add(field.Bytes.ToArray());
            }
        }

        foreach (byte[] logRecordPayload in logRecordPayloads)
        {
            NormalizedOtelLogRecord normalized = ParseProtobufLogRecord(logRecordPayload, resourceAttributes, scopeName, scopeVersion);
            appendLine(Serialize(normalized));
            recordCount++;
        }
    }

    private static void ParseScope(ReadOnlySpan<byte> data, out string name, out string version)
    {
        name = string.Empty;
        version = string.Empty;
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.WireType != ProtoWireType.LengthDelimited)
            {
                continue;
            }

            if (field.Number == 1)
            {
                name = DecodeUtf8(field.Bytes);
            }
            else if (field.Number == 2)
            {
                version = DecodeUtf8(field.Bytes);
            }
        }
    }

    private static NormalizedOtelLogRecord ParseProtobufLogRecord(
        ReadOnlySpan<byte> data,
        IReadOnlyDictionary<string, string> resourceAttributes,
        string scopeName,
        string scopeVersion)
    {
        ulong timeUnixNano = 0;
        ulong observedTimeUnixNano = 0;
        string severityNumber = string.Empty;
        string severityText = string.Empty;
        string body = string.Empty;
        string traceId = string.Empty;
        string spanId = string.Empty;
        string eventName = string.Empty;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            switch (field.Number)
            {
                case 1:
                    timeUnixNano = ReadUnsignedInteger(field);
                    break;
                case 2:
                    severityNumber = ReadUnsignedInteger(field).ToString(CultureInfo.InvariantCulture);
                    break;
                case 3 when field.WireType == ProtoWireType.LengthDelimited:
                    severityText = DecodeUtf8(field.Bytes);
                    break;
                case 5 when field.WireType == ProtoWireType.LengthDelimited:
                    body = ParseAnyValue(field.Bytes);
                    break;
                case 6 when field.WireType == ProtoWireType.LengthDelimited:
                    ParseKeyValue(field.Bytes, attributes);
                    break;
                case 9 when field.WireType == ProtoWireType.LengthDelimited:
                    traceId = ToHex(field.Bytes);
                    break;
                case 10 when field.WireType == ProtoWireType.LengthDelimited:
                    spanId = ToHex(field.Bytes);
                    break;
                case 11:
                    observedTimeUnixNano = ReadUnsignedInteger(field);
                    break;
                case 12 when field.WireType == ProtoWireType.LengthDelimited:
                    eventName = DecodeUtf8(field.Bytes);
                    break;
            }
        }

        string severity = string.IsNullOrWhiteSpace(severityText)
            ? FormatSeverityNumber(severityNumber)
            : severityText;

        return new NormalizedOtelLogRecord(
            Time: FormatUnixNano(timeUnixNano),
            ObservedAt: FormatUnixNano(observedTimeUnixNano),
            Severity: severity,
            SeverityNumber: severityNumber,
            ServiceName: resourceAttributes.GetValueOrDefault("service.name") ?? string.Empty,
            ScopeName: scopeName,
            ScopeVersion: scopeVersion,
            TraceId: traceId,
            SpanId: spanId,
            EventName: eventName,
            Attributes: FormatAttributeMap(attributes),
            Resource: FormatAttributeMap(resourceAttributes),
            Body: body);
    }

    private static void ParseKeyValue(ReadOnlySpan<byte> data, IDictionary<string, string> attributes)
    {
        string key = string.Empty;
        string value = string.Empty;
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                key = DecodeUtf8(field.Bytes);
            }
            else if (field.Number == 2 && field.WireType == ProtoWireType.LengthDelimited)
            {
                value = ParseAnyValue(field.Bytes);
            }
        }

        if (key.Length > 0)
        {
            attributes[key] = value;
        }
    }

    private static string ParseAnyValue(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            switch (field.Number)
            {
                case 1 when field.WireType == ProtoWireType.LengthDelimited:
                    return DecodeUtf8(field.Bytes);
                case 2:
                    return field.Varint == 0 ? "false" : "true";
                case 3:
                    return unchecked((long)field.Varint).ToString(CultureInfo.InvariantCulture);
                case 4 when field.WireType == ProtoWireType.Fixed64:
                    return BitConverter.Int64BitsToDouble(unchecked((long)field.Fixed64)).ToString(CultureInfo.InvariantCulture);
                case 5 when field.WireType == ProtoWireType.LengthDelimited:
                    return ParseArrayValue(field.Bytes);
                case 6 when field.WireType == ProtoWireType.LengthDelimited:
                    return FormatAttributeMap(ParseKeyValueList(field.Bytes));
                case 7 when field.WireType == ProtoWireType.LengthDelimited:
                    return ToHex(field.Bytes);
            }
        }

        return string.Empty;
    }

    private static string ParseArrayValue(ReadOnlySpan<byte> data)
    {
        var values = new List<string>();
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                values.Add(ParseAnyValue(field.Bytes));
            }
        }

        return "[" + string.Join(", ", values) + "]";
    }

    private static Dictionary<string, string> ParseKeyValueList(ReadOnlySpan<byte> data)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var reader = new ProtoReader(data);

        while (reader.TryReadField(out ProtoField field))
        {
            if (field.Number == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                ParseKeyValue(field.Bytes, values);
            }
        }

        return values;
    }

    private static ulong ReadUnsignedInteger(ProtoField field)
    {
        return field.WireType switch
        {
            ProtoWireType.Varint => field.Varint,
            ProtoWireType.Fixed64 => field.Fixed64,
            ProtoWireType.Fixed32 => field.Fixed32,
            _ => 0
        };
    }

    private static string Serialize(NormalizedOtelLogRecord record)
    {
        using var memory = new MemoryStream();

        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();
            writer.WriteString("time", record.Time);
            writer.WriteString("observedAt", record.ObservedAt);
            writer.WriteString("severity", record.Severity);
            writer.WriteString("severityNumber", record.SeverityNumber);
            writer.WriteString("serviceName", record.ServiceName);
            writer.WriteString("scopeName", record.ScopeName);
            writer.WriteString("scopeVersion", record.ScopeVersion);
            writer.WriteString("traceId", record.TraceId);
            writer.WriteString("spanId", record.SpanId);
            writer.WriteString("eventName", record.EventName);
            writer.WriteString("attributes", record.Attributes);
            writer.WriteString("resource", record.Resource);
            writer.WriteString("body", record.Body);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string FormatAttributeMap(IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
        {
            return string.Empty;
        }

        using var memory = new MemoryStream();

        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();

            foreach (KeyValuePair<string, string> attribute in attributes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(attribute.Key, attribute.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string FormatSeverityNumber(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int severityNumber))
        {
            return string.Empty;
        }

        return severityNumber switch
        {
            >= 1 and <= 4 => "TRACE",
            >= 5 and <= 8 => "DEBUG",
            >= 9 and <= 12 => "INFO",
            >= 13 and <= 16 => "WARN",
            >= 17 and <= 20 => "ERROR",
            >= 21 and <= 24 => "FATAL",
            _ => string.Empty
        };
    }

    private static string FormatUnixNano(string rawUnixNano)
    {
        if (!ulong.TryParse(rawUnixNano, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
        {
            return string.Empty;
        }

        return FormatUnixNano(value);
    }

    private static string FormatUnixNano(ulong unixNano)
    {
        if (unixNano == 0)
        {
            return string.Empty;
        }

        ulong ticks = unixNano / 100;

        if (ticks > (ulong)(DateTimeOffset.MaxValue - UnixEpoch).Ticks)
        {
            return string.Empty;
        }

        return UnixEpoch
            .AddTicks((long)ticks)
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static string NormalizeHexId(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..].ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static string ToHex(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private readonly record struct NormalizedOtelLogRecord(
        string Time,
        string ObservedAt,
        string Severity,
        string SeverityNumber,
        string ServiceName,
        string ScopeName,
        string ScopeVersion,
        string TraceId,
        string SpanId,
        string EventName,
        string Attributes,
        string Resource,
        string Body);

    private ref struct ProtoReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public bool TryReadField(out ProtoField field)
        {
            field = default;

            if (_offset >= _data.Length)
            {
                return false;
            }

            ulong tag = ReadVarint();
            int number = (int)(tag >> 3);
            var wireType = (ProtoWireType)(tag & 0x7);

            switch (wireType)
            {
                case ProtoWireType.Varint:
                    field = new ProtoField(number, wireType, ReadVarint(), 0, 0, ReadOnlySpan<byte>.Empty);
                    return true;
                case ProtoWireType.Fixed64:
                    EnsureAvailable(8);
                    ulong fixed64 = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_offset, 8));
                    _offset += 8;
                    field = new ProtoField(number, wireType, 0, fixed64, 0, ReadOnlySpan<byte>.Empty);
                    return true;
                case ProtoWireType.LengthDelimited:
                    int length = checked((int)ReadVarint());
                    EnsureAvailable(length);
                    ReadOnlySpan<byte> bytes = _data.Slice(_offset, length);
                    _offset += length;
                    field = new ProtoField(number, wireType, 0, 0, 0, bytes);
                    return true;
                case ProtoWireType.Fixed32:
                    EnsureAvailable(4);
                    uint fixed32 = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, 4));
                    _offset += 4;
                    field = new ProtoField(number, wireType, 0, 0, fixed32, ReadOnlySpan<byte>.Empty);
                    return true;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type '{wireType}'.");
            }
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            int shift = 0;

            while (_offset < _data.Length && shift <= 63)
            {
                byte current = _data[_offset++];
                value |= ((ulong)(current & 0x7F)) << shift;

                if ((current & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
            }

            throw new InvalidDataException("Invalid protobuf varint.");
        }

        private void EnsureAvailable(int length)
        {
            if (length < 0 || _offset + length > _data.Length)
            {
                throw new InvalidDataException("Invalid protobuf length-delimited field.");
            }
        }
    }

    private readonly ref struct ProtoField
    {
        public ProtoField(
            int number,
            ProtoWireType wireType,
            ulong varint,
            ulong fixed64,
            uint fixed32,
            ReadOnlySpan<byte> bytes)
        {
            Number = number;
            WireType = wireType;
            Varint = varint;
            Fixed64 = fixed64;
            Fixed32 = fixed32;
            Bytes = bytes;
        }

        public int Number
        {
            get;
        }

        public ProtoWireType WireType
        {
            get;
        }

        public ulong Varint
        {
            get;
        }

        public ulong Fixed64
        {
            get;
        }

        public uint Fixed32
        {
            get;
        }

        public ReadOnlySpan<byte> Bytes
        {
            get;
        }
    }

    private enum ProtoWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5
    }
}
