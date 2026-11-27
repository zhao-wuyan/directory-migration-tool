using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace MigrationCore.Services.Ai;

/// <summary>
/// AI 工具调用参数的宽松规范化器与友好错误格式化器。
/// 目标：在保持发给模型的 JSON Schema 清晰标准的前提下，宽容处理常见 LLM 传参习惯：
/// 1. 可选参数传 "" / "   " / "null" / "none" / "undefined" / "N/A" / null 时自动视作未传（触发 C# 默认值或 null）；
/// 2. 嵌套对象数组（如 items[].nodeId / items[].priority）中的空串、字符串数字（"1"）、浮点整数（1.0）自动按 Schema 规范化；
/// 3. GUID 参数自动去除首尾空白/花括号，字符串数组参数兼容单字符串传入；
/// 4. 真正无法解析的非法参数给出可读的中文提示与必填/可选参数清单，帮助模型一步修正而不陷入死循环。
/// </summary>
internal static class AiToolArgumentNormalizer
{
    private static readonly JsonElement NullJsonElement = JsonDocument.Parse("null").RootElement.Clone();

    public static AIFunctionArguments NormalizeArguments(
        IDictionary<string, object?>? rawArgs,
        AIFunction targetFunction)
    {
        var result = new AIFunctionArguments();
        if (rawArgs is null || rawArgs.Count == 0)
        {
            return result;
        }

        JsonElement rootSchema = targetFunction.JsonSchema;
        Dictionary<string, JsonElement> properties = ExtractPropertiesMap(rootSchema);
        HashSet<string> required = ExtractRequiredSet(rootSchema);

        foreach (KeyValuePair<string, object?> kv in rawArgs)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                continue;
            }

            bool hasSchema = properties.TryGetValue(kv.Key, out JsonElement propSchema);
            bool isRequired = required.Contains(kv.Key);

            // 顶层可选参数传 null / "" / "null" / "none" 等占位词时直接省略，交由 C# 参数默认值处理
            if (IsNullOrEmptySentinelValue(kv.Value))
            {
                if (!isRequired)
                {
                    continue;
                }

                if (hasSchema && SchemaAllowsNull(propSchema, rootSchema))
                {
                    result[kv.Key] = null;
                    continue;
                }
            }

            if (hasSchema)
            {
                result[kv.Key] = NormalizeValue(kv.Value, propSchema, rootSchema);
            }
            else
            {
                result[kv.Key] = kv.Value;
            }
        }

        return result;
    }

    public static string FormatToolInvocationError(string toolName, Exception ex, JsonElement toolSchema)
    {
        string raw = AiPathSanitizer.SanitizeUntrustedText(ex.Message, 240, preserveNewLines: false);

        if (ex is not (JsonException or ArgumentException or FormatException or InvalidCastException))
        {
            return raw;
        }

        Dictionary<string, JsonElement> props = ExtractPropertiesMap(toolSchema);
        HashSet<string> required = ExtractRequiredSet(toolSchema);

        string[] reqNames = props.Keys.Where(k => required.Contains(k)).ToArray();
        string[] optNames = props.Keys.Where(k => !required.Contains(k)).ToArray();

        string reqText = reqNames.Length > 0 ? string.Join(", ", reqNames) : "无";
        string optText = optNames.Length > 0 ? string.Join(", ", optNames) : "无";

        return $"工具 '{toolName}' 参数格式错误（{raw}）。提示：必填参数=[{reqText}]，可选参数=[{optText}]（可选参数无值时请直接省略或传 null，不要传空字符串；GUID 须为标准 UUID，节点/优先级须为整数）。";
    }

    public static bool IsEmptyOrNullSentinelString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string trimmed = text.Trim();
        return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("nil", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNullOrEmptySentinelValue(object? value) => value switch
    {
        null => true,
        string s => IsEmptyOrNullSentinelString(s),
        JsonElement je when je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonElement je when je.ValueKind == JsonValueKind.String => IsEmptyOrNullSentinelString(je.GetString()),
        _ => false
    };

    private static object? NormalizeValue(object? value, JsonElement schema, JsonElement rootSchema)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement je)
        {
            return NormalizeJsonElement(je, schema, rootSchema);
        }

        if (value is string str)
        {
            JsonElement resolved = ResolveSchema(schema, rootSchema);
            if (IsEmptyOrNullSentinelString(str))
            {
                if (SchemaAllowsNull(resolved, rootSchema)
                    || SchemaExpectsNonStringScalar(resolved, rootSchema))
                {
                    return null;
                }
                if (SchemaExpectsType(resolved, rootSchema, "array"))
                {
                    return Array.Empty<string>();
                }
                return str;
            }

            if (SchemaIsUuid(resolved, rootSchema) && Guid.TryParse(str.Trim(), out Guid parsedGuid))
            {
                return parsedGuid.ToString("D");
            }

            if (SchemaExpectsType(resolved, rootSchema, "integer") && !SchemaExpectsType(resolved, rootSchema, "string"))
            {
                if (TryParseIntegralNumber(str, out long parsedLong))
                {
                    return parsedLong;
                }
            }

            if (SchemaExpectsType(resolved, rootSchema, "array") && !SchemaExpectsType(resolved, rootSchema, "string"))
            {
                return SplitStringToArray(str);
            }

            return str;
        }

        return value;
    }

    private static JsonElement NormalizeJsonElement(JsonElement el, JsonElement schema, JsonElement rootSchema)
    {
        JsonElement resolved = ResolveSchema(schema, rootSchema);

        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return NullJsonElement;

            case JsonValueKind.String:
            {
                string s = el.GetString() ?? string.Empty;
                if (IsEmptyOrNullSentinelString(s))
                {
                    if (SchemaAllowsNull(resolved, rootSchema)
                        || SchemaExpectsNonStringScalar(resolved, rootSchema))
                    {
                        return NullJsonElement;
                    }
                    if (SchemaExpectsType(resolved, rootSchema, "array") && !SchemaExpectsType(resolved, rootSchema, "string"))
                    {
                        return JsonSerializer.SerializeToElement(Array.Empty<string>());
                    }
                    return el;
                }

                if (SchemaIsUuid(resolved, rootSchema))
                {
                    string trimmed = s.Trim();
                    if (Guid.TryParse(trimmed, out Guid g))
                    {
                        return JsonSerializer.SerializeToElement(g.ToString("D"));
                    }
                    return el;
                }

                if (SchemaExpectsType(resolved, rootSchema, "integer") && !SchemaExpectsType(resolved, rootSchema, "string"))
                {
                    if (TryParseIntegralNumber(s, out long iv))
                    {
                        return JsonSerializer.SerializeToElement(iv);
                    }
                    return el;
                }

                if (SchemaExpectsType(resolved, rootSchema, "number") && !SchemaExpectsType(resolved, rootSchema, "string"))
                {
                    if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) && double.IsFinite(dv))
                    {
                        return JsonSerializer.SerializeToElement(dv);
                    }
                    return el;
                }

                if (SchemaExpectsType(resolved, rootSchema, "boolean") && !SchemaExpectsType(resolved, rootSchema, "string"))
                {
                    if (bool.TryParse(s.Trim(), out bool bv))
                    {
                        return JsonSerializer.SerializeToElement(bv);
                    }
                    return el;
                }

                if (SchemaExpectsType(resolved, rootSchema, "array") && !SchemaExpectsType(resolved, rootSchema, "string"))
                {
                    return JsonSerializer.SerializeToElement(SplitStringToArray(s));
                }

                return el;
            }

            case JsonValueKind.Number:
            {
                if (SchemaExpectsType(resolved, rootSchema, "integer")
                    && !el.TryGetInt64(out _)
                    && el.TryGetDouble(out double d)
                    && double.IsFinite(d)
                    && Math.Truncate(d) == d
                    && d >= long.MinValue
                    && d <= long.MaxValue)
                {
                    return JsonSerializer.SerializeToElement((long)d);
                }
                return el;
            }

            case JsonValueKind.Array:
            {
                bool hasItemsSchema = TryGetItemsSchema(resolved, rootSchema, out JsonElement itemsSchema);
                var arr = new JsonArray();
                foreach (JsonElement item in el.EnumerateArray())
                {
                    JsonElement normItem = hasItemsSchema
                        ? NormalizeJsonElement(item, itemsSchema, rootSchema)
                        : item;
                    arr.Add(JsonNode.Parse(normItem.GetRawText()));
                }
                return JsonSerializer.SerializeToElement(arr);
            }

            case JsonValueKind.Object:
            {
                Dictionary<string, JsonElement> childProps = ExtractPropertiesMap(resolved, rootSchema);
                HashSet<string> childRequired = ExtractRequiredSet(resolved, rootSchema);

                var obj = new JsonObject();
                foreach (JsonProperty prop in el.EnumerateObject())
                {
                    bool hasChildSchema = childProps.TryGetValue(prop.Name, out JsonElement childSchema);
                    bool isChildRequired = childRequired.Contains(prop.Name);

                    if (hasChildSchema && IsNullOrEmptySentinelValue(prop.Value))
                    {
                        JsonElement resolvedChild = ResolveSchema(childSchema, rootSchema);
                        if (SchemaAllowsNull(resolvedChild, rootSchema))
                        {
                            obj[prop.Name] = null;
                            continue;
                        }

                        // 非可空但非必填的字段（例如 int Priority = 1 或 string Kind = ""）传 "" / null 时省略，让反序列化器使用默认值
                        if (!isChildRequired)
                        {
                            continue;
                        }
                    }

                    JsonElement normProp = hasChildSchema
                        ? NormalizeJsonElement(prop.Value, childSchema, rootSchema)
                        : prop.Value;
                    obj[prop.Name] = JsonNode.Parse(normProp.GetRawText());
                }

                return JsonSerializer.SerializeToElement(obj);
            }

            default:
                return el;
        }
    }

    private static bool TryParseIntegralNumber(string raw, out long value)
    {
        string trimmed = raw.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            && double.IsFinite(d)
            && Math.Truncate(d) == d
            && d >= long.MinValue
            && d <= long.MaxValue)
        {
            value = (long)d;
            return true;
        }
        value = 0;
        return false;
    }

    private static string[] SplitStringToArray(string raw) =>
        raw.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !IsEmptyOrNullSentinelString(s))
            .ToArray();

    private static JsonElement ResolveSchema(JsonElement schema, JsonElement rootSchema)
    {
        JsonElement current = schema;
        for (int depth = 0; depth < 8; depth++)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return current;
            }

            if (current.TryGetProperty("$ref", out JsonElement refEl)
                && refEl.ValueKind == JsonValueKind.String)
            {
                string? refPath = refEl.GetString();
                if (!string.IsNullOrEmpty(refPath)
                    && TryResolveJsonPointer(rootSchema, refPath, out JsonElement target))
                {
                    current = target;
                    continue;
                }
            }

            break;
        }
        return current;
    }

    private static bool TryResolveJsonPointer(JsonElement root, string pointer, out JsonElement target)
    {
        target = default;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = pointer[2..].Split('/');
        JsonElement cur = root;
        foreach (string rawSeg in segments)
        {
            string seg = rawSeg.Replace("~1", "/").Replace("~0", "~");
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
            {
                return false;
            }
        }
        target = cur;
        return true;
    }

    private static Dictionary<string, JsonElement> ExtractPropertiesMap(JsonElement schema, JsonElement? rootSchema = null)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        JsonElement root = rootSchema ?? schema;

        foreach (JsonElement branch in EnumerateSchemaBranches(schema, root))
        {
            if (branch.ValueKind == JsonValueKind.Object
                && branch.TryGetProperty("properties", out JsonElement propsEl)
                && propsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in propsEl.EnumerateObject())
                {
                    map[prop.Name] = prop.Value;
                }
            }
        }
        return map;
    }

    private static HashSet<string> ExtractRequiredSet(JsonElement schema, JsonElement? rootSchema = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JsonElement root = rootSchema ?? schema;

        foreach (JsonElement branch in EnumerateSchemaBranches(schema, root))
        {
            if (branch.ValueKind == JsonValueKind.Object
                && branch.TryGetProperty("required", out JsonElement reqEl)
                && reqEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in reqEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    {
                        set.Add(name);
                    }
                }
            }
        }
        return set;
    }

    private static bool TryGetItemsSchema(JsonElement schema, JsonElement rootSchema, out JsonElement itemsSchema)
    {
        foreach (JsonElement branch in EnumerateSchemaBranches(schema, rootSchema))
        {
            if (branch.ValueKind == JsonValueKind.Object
                && branch.TryGetProperty("items", out JsonElement itemsEl))
            {
                itemsSchema = itemsEl;
                return true;
            }
        }
        itemsSchema = default;
        return false;
    }

    private static bool SchemaAllowsNull(JsonElement schema, JsonElement rootSchema) =>
        SchemaExpectsType(schema, rootSchema, "null");

    private static bool SchemaExpectsNonStringScalar(JsonElement schema, JsonElement rootSchema)
    {
        if (SchemaIsUuid(schema, rootSchema))
        {
            return true;
        }
        bool expectsString = SchemaExpectsType(schema, rootSchema, "string");
        if (expectsString)
        {
            return false;
        }
        return SchemaExpectsType(schema, rootSchema, "integer")
            || SchemaExpectsType(schema, rootSchema, "number")
            || SchemaExpectsType(schema, rootSchema, "boolean");
    }

    private static bool SchemaIsUuid(JsonElement schema, JsonElement rootSchema)
    {
        foreach (JsonElement branch in EnumerateSchemaBranches(schema, rootSchema))
        {
            if (branch.ValueKind == JsonValueKind.Object
                && branch.TryGetProperty("format", out JsonElement fmtEl)
                && fmtEl.ValueKind == JsonValueKind.String
                && string.Equals(fmtEl.GetString(), "uuid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SchemaExpectsType(JsonElement schema, JsonElement rootSchema, string targetType)
    {
        foreach (JsonElement branch in EnumerateSchemaBranches(schema, rootSchema))
        {
            if (branch.ValueKind != JsonValueKind.Object || !branch.TryGetProperty("type", out JsonElement typeEl))
            {
                continue;
            }

            if (typeEl.ValueKind == JsonValueKind.String
                && string.Equals(typeEl.GetString(), targetType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (typeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in typeEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String
                        && string.Equals(t.GetString(), targetType, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateSchemaBranches(JsonElement schema, JsonElement rootSchema)
    {
        JsonElement resolved = ResolveSchema(schema, rootSchema);
        yield return resolved;

        if (resolved.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (string combinator in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (resolved.TryGetProperty(combinator, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement sub in arr.EnumerateArray())
                {
                    yield return ResolveSchema(sub, rootSchema);
                }
            }
        }
    }
}
