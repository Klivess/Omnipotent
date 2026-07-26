using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.KliveLLM;

/// <summary>
/// Converts Gemma's native textual function-call protocol into the OpenAI-compatible
/// <c>tool_calls</c> shape used by the rest of Omnipotent. Some OpenAI-compatible providers
/// return Gemma's special tokens in <c>message.content</c> instead of translating them at the
/// gateway:
///
/// <code>
/// &lt;|tool_call&gt;call:tool_name{argument:&lt;|"|&gt;value&lt;|"|&gt;}&lt;tool_call|&gt;
/// </code>
///
/// This adapter is deliberately fail-closed. It only accepts tools present in the exact offered
/// tool list, validates folded <c>:op:</c> suffixes against the offered schema, produces a JSON
/// object for arguments, and returns no partial calls if any envelope is malformed.
/// </summary>
internal static class GemmaToolCallCompatibility
{
    internal sealed record Result(
        bool Detected,
        bool Success,
        string Content,
        List<HFWrapper.HFToolCall>? ToolCalls,
        string? Error,
        bool Adapted);

    private sealed record MarkerSyntax(string Start, string[] Ends);
    private sealed record MarkerMatch(int Index, MarkerSyntax Syntax);

    private static readonly MarkerSyntax[] MarkerSyntaxes =
    [
        new("<|tool_call>", ["<tool_call|>"]),
        new("<|tool_call|>", ["<|/tool_call|>", "</tool_call>"]),
        new("<tool_call>", ["</tool_call>"]),
        // Observed from gateways which rewrite Gemma's vertical-bar sentinels.
        new("<{tool_call}>", ["<{tool_call}>"]),
    ];

    private static readonly string[] ResponseSentinels =
    [
        "<|tool_response>",
        "<tool_response|>",
        "<|tool_response|>",
        "<|/tool_response|>",
        "<tool_response>",
        "</tool_response>",
    ];

    /// <summary>
    /// Preserves a provider's real structured calls unchanged. Text parsing is a compatibility
    /// fallback only when the provider returned no <c>tool_calls</c> array.
    /// </summary>
    internal static Result Normalize(
        string? content,
        List<HFWrapper.HFToolCall>? nativeToolCalls,
        IReadOnlyList<HFWrapper.HFTool> offeredTools)
    {
        string text = content ?? string.Empty;
        if (nativeToolCalls is { Count: > 0 })
            return new Result(false, true, text, nativeToolCalls, null, false);

        return ParseTextCalls(text, offeredTools);
    }

    private static Result ParseTextCalls(string text, IReadOnlyList<HFWrapper.HFTool> offeredTools)
    {
        MarkerMatch? first = FindNextMarker(text, 0);
        if (first == null)
            return new Result(false, true, text, null, null, false);

        var toolsByName = (offeredTools ?? Array.Empty<HFWrapper.HFTool>())
            .Where(t => !string.IsNullOrWhiteSpace(t?.function?.name))
            .GroupBy(t => t.function.name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var calls = new List<HFWrapper.HFToolCall>();
        var visibleContent = new StringBuilder();
        int cursor = 0;

        while (true)
        {
            MarkerMatch? marker = FindNextMarker(text, cursor);
            if (marker == null)
            {
                visibleContent.Append(text, cursor, text.Length - cursor);
                break;
            }

            visibleContent.Append(text, cursor, marker.Index - cursor);
            int bodyStart = marker.Index + marker.Syntax.Start.Length;
            if (!TryFindEnd(text, bodyStart, marker.Syntax.Ends, out int endIndex, out string? endToken))
                return Failure(text, $"Gemma tool-call marker at character {marker.Index} has no closing sentinel.");

            string envelope = text[bodyStart..endIndex].Trim();
            if (!TryParseEnvelope(envelope, toolsByName, calls.Count, out var call, out string? error))
                return Failure(text, error ?? "Gemma tool-call envelope is malformed.");

            calls.Add(call!);
            cursor = endIndex + endToken!.Length;
        }

        if (calls.Count == 0)
            return Failure(text, "Gemma tool-call markers were present but no complete calls were found.");

        string cleaned = StripResponseSentinels(visibleContent.ToString()).Trim();
        return new Result(true, true, cleaned, calls, null, true);
    }

    private static Result Failure(string original, string error) =>
        new(true, false, string.Empty, null,
            $"Gemma textual tool-call protocol error: {error}", false);

    /// <summary>
    /// Converts completed compatibility-tool exchanges back into Gemma's native assistant-turn
    /// handshake before the model is queried again. The canonical session remains OpenAI-shaped for
    /// dispatch, validation and compaction; only the provider-facing copy is folded.
    ///
    /// Gemma stops generation at <c>&lt;|tool_response&gt;</c> and expects the application to append
    /// the execution result before resuming the model. A gateway which returned the raw textual call
    /// cannot be trusted to reconstruct that continuation from a synthetic OpenAI
    /// assistant/tool-message pair, so we do it explicitly here.
    /// </summary>
    internal static List<HFWrapper.HFMessage> PrepareContinuationMessages(
        IReadOnlyList<HFWrapper.HFMessage> messages)
    {
        if (messages == null || messages.Count == 0
            || !messages.Any(message => message?.GemmaTextualToolTurn == true))
            return messages?.ToList() ?? new List<HFWrapper.HFMessage>();

        var prepared = new List<HFWrapper.HFMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            var assistant = messages[i];
            if (assistant?.GemmaTextualToolTurn != true
                || assistant.tool_calls == null
                || assistant.tool_calls.Count == 0)
            {
                prepared.Add(assistant);
                continue;
            }

            var expectedIds = assistant.tool_calls
                .Where(call => !string.IsNullOrWhiteSpace(call?.id))
                .Select(call => call.id)
                .ToHashSet(StringComparer.Ordinal);
            var resultsById = new Dictionary<string, HFWrapper.HFMessage>(StringComparer.Ordinal);
            int afterResults = i + 1;
            while (afterResults < messages.Count)
            {
                var candidate = messages[afterResults];
                if (candidate?.role != "tool"
                    || string.IsNullOrWhiteSpace(candidate.tool_call_id)
                    || !expectedIds.Contains(candidate.tool_call_id))
                    break;

                resultsById.TryAdd(candidate.tool_call_id, candidate);
                afterResults++;
            }

            // Never manufacture a partial Gemma continuation. The ordinary protocol validator should
            // reject an incomplete batch rather than letting the model see a response for only some of
            // the calls it made.
            if (expectedIds.Count == 0 || resultsById.Count != expectedIds.Count)
            {
                prepared.Add(assistant);
                continue;
            }

            prepared.Add(new HFWrapper.HFMessage
            {
                role = "assistant",
                content = BuildContinuationContent(assistant, resultsById),
            });
            i = afterResults - 1;
        }

        return prepared;
    }

    private static string BuildContinuationContent(
        HFWrapper.HFMessage assistant,
        IReadOnlyDictionary<string, HFWrapper.HFMessage> resultsById)
    {
        var output = new StringBuilder();
        string visibleContent = HFWrapper.ContentToText(assistant.content).Trim();
        if (visibleContent.Length > 0)
            output.Append(visibleContent).AppendLine();

        foreach (var call in assistant.tool_calls)
        {
            string functionName = string.IsNullOrWhiteSpace(call.GemmaAuthoredName)
                ? call.function?.name ?? string.Empty
                : call.GemmaAuthoredName;
            JObject arguments;
            try
            {
                arguments = JObject.Parse(call.function?.arguments ?? "{}");
            }
            catch (JsonException)
            {
                // The inbound adapter always emits a JSON object. This defensive fallback keeps a
                // damaged in-memory message readable to Gemma without injecting malformed grammar.
                arguments = new JObject
                {
                    ["arguments_json"] = call.function?.arguments ?? string.Empty,
                };
            }

            output.Append("<|tool_call>call:")
                .Append(functionName)
                .Append(WriteGemmaValue(arguments))
                .Append("<tool_call|>");
        }

        foreach (var call in assistant.tool_calls)
        {
            var result = resultsById[call.id];
            string functionName = string.IsNullOrWhiteSpace(call.GemmaAuthoredName)
                ? call.function?.name ?? result.name ?? string.Empty
                : call.GemmaAuthoredName;
            output.Append("<|tool_response>response:")
                .Append(functionName)
                .Append("{result:")
                .Append(GemmaQuote(HFWrapper.ContentToText(result.content)))
                .Append("}<tool_response|>");
        }

        return output.ToString();
    }

    private static string WriteGemmaValue(JToken? token)
    {
        if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
            return "null";

        if (token is JObject obj)
            return "{" + string.Join(",", obj.Properties()
                .Select(property => WriteGemmaKey(property.Name) + ":" + WriteGemmaValue(property.Value))) + "}";

        if (token is JArray array)
            return "[" + string.Join(",", array.Select(WriteGemmaValue)) + "]";

        if (token.Type == JTokenType.Boolean)
            return token.Value<bool>() ? "true" : "false";

        if (token.Type is JTokenType.Integer or JTokenType.Float)
            return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture) ?? "0";

        return GemmaQuote(token.Type == JTokenType.String
            ? token.Value<string>() ?? string.Empty
            : token.ToString(Formatting.None));
    }

    private static string WriteGemmaKey(string key)
    {
        if (key.Length > 0
            && (char.IsLetter(key[0]) || key[0] == '_')
            && key.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_'))
            return key;
        return GemmaQuote(key);
    }

    private static string GemmaQuote(string value)
    {
        // Gemma's string delimiter is a special token rather than an escapable character. Neutralize
        // any control-token-looking text returned by a tool so data can never close the response block.
        string safe = (value ?? string.Empty)
            .Replace("<|", "\\u003C|", StringComparison.Ordinal)
            .Replace("<tool", "\\u003Ctool", StringComparison.Ordinal);
        return "<|\"|>" + safe + "<|\"|>";
    }

    private static MarkerMatch? FindNextMarker(string text, int startAt)
    {
        MarkerMatch? best = null;
        foreach (var syntax in MarkerSyntaxes)
        {
            int idx = text.IndexOf(syntax.Start, startAt, StringComparison.Ordinal);
            if (idx < 0 || (best != null && idx >= best.Index)) continue;
            best = new MarkerMatch(idx, syntax);
        }
        return best;
    }

    private static bool TryFindEnd(string text, int startAt, IReadOnlyList<string> candidates,
        out int index, out string? token)
    {
        index = -1;
        token = null;
        foreach (string candidate in candidates)
        {
            int found = text.IndexOf(candidate, startAt, StringComparison.Ordinal);
            if (found < 0 || (index >= 0 && found >= index)) continue;
            index = found;
            token = candidate;
        }
        return index >= 0;
    }

    private static bool TryParseEnvelope(
        string envelope,
        IReadOnlyDictionary<string, HFWrapper.HFTool> toolsByName,
        int callIndex,
        out HFWrapper.HFToolCall? call,
        out string? error)
    {
        call = null;
        error = null;

        if (!envelope.StartsWith("call:", StringComparison.Ordinal))
        {
            error = "an envelope did not begin with 'call:'.";
            return false;
        }

        int objectStart = envelope.IndexOf('{', "call:".Length);
        int objectEnd = envelope.LastIndexOf('}');
        if (objectStart <= "call:".Length || objectEnd < objectStart
            || envelope[(objectEnd + 1)..].Trim().Length > 0)
        {
            error = "a call must contain one outer {arguments} object.";
            return false;
        }

        string authoredName = envelope["call:".Length..objectStart].Trim();
        if (!TryResolveTool(authoredName, toolsByName, out string? toolName,
                out string? encodedOp, out JObject? schema, out error))
            return false;

        string argumentsBody = envelope[(objectStart + 1)..objectEnd];
        if (!GemmaArgumentParser.TryParse(argumentsBody, schema!, out JObject? arguments, out error))
        {
            error = $"arguments for '{toolName}' could not be parsed: {error}";
            return false;
        }

        if (!string.IsNullOrEmpty(encodedOp))
        {
            if (arguments!.TryGetValue("op", StringComparison.Ordinal, out JToken? suppliedOp))
            {
                if (suppliedOp.Type != JTokenType.String
                    || !string.Equals(suppliedOp.Value<string>(), encodedOp, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"'{authoredName}' conflicts with the 'op' value in its arguments.";
                    return false;
                }
            }
            arguments!["op"] = encodedOp;
        }

        call = new HFWrapper.HFToolCall
        {
            // Keep the conventional OpenAI call_ prefix because the synthesized assistant turn
            // is sent back through an OpenAI-compatible gateway on the next tool round.
            id = $"call_gemma_{Guid.NewGuid():N}_{callIndex}",
            type = "function",
            function = new HFWrapper.HFFunctionCall
            {
                name = toolName!,
                arguments = arguments!.ToString(Formatting.None),
            },
            GemmaAuthoredName = authoredName,
        };
        return true;
    }

    private static bool TryResolveTool(
        string authoredName,
        IReadOnlyDictionary<string, HFWrapper.HFTool> toolsByName,
        out string? toolName,
        out string? encodedOp,
        out JObject? schema,
        out string? error)
    {
        toolName = null;
        encodedOp = null;
        schema = null;
        error = null;

        string candidate = authoredName;
        const string kliveAgentNamespace = "kliveagent:";
        if (candidate.StartsWith(kliveAgentNamespace, StringComparison.OrdinalIgnoreCase))
            candidate = candidate[kliveAgentNamespace.Length..];

        const string opSeparator = ":op:";
        int opIndex = candidate.IndexOf(opSeparator, StringComparison.OrdinalIgnoreCase);
        if (opIndex >= 0)
        {
            encodedOp = candidate[(opIndex + opSeparator.Length)..].Trim();
            candidate = candidate[..opIndex].Trim();
            if (encodedOp.Length == 0 || encodedOp.Contains(':'))
            {
                error = $"'{authoredName}' has an invalid encoded operation.";
                return false;
            }
        }

        if (!toolsByName.TryGetValue(candidate, out HFWrapper.HFTool? tool))
        {
            error = $"'{authoredName}' does not resolve to a tool offered in this request.";
            return false;
        }

        schema = SchemaOf(tool);
        if (!string.IsNullOrEmpty(encodedOp))
        {
            var allowedOps = (schema["properties"]?["op"]?["enum"] as JArray)?
                .Values<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowedOps == null || !allowedOps.Contains(encodedOp))
            {
                error = $"operation '{encodedOp}' is not allowed by the offered '{candidate}' schema.";
                return false;
            }
        }

        toolName = candidate;
        return true;
    }

    private static JObject SchemaOf(HFWrapper.HFTool tool)
    {
        if (tool.function?.parameters == null) return new JObject();
        return tool.function.parameters is JObject schema
            ? (JObject)schema.DeepClone()
            : JObject.Parse(JsonConvert.SerializeObject(tool.function.parameters));
    }

    private static string StripResponseSentinels(string content)
    {
        string cleaned = content;
        foreach (string sentinel in ResponseSentinels)
            cleaned = cleaned.Replace(sentinel, string.Empty, StringComparison.Ordinal);
        return cleaned;
    }

    /// <summary>
    /// Small schema-aware reader for Gemma's JSON-like argument grammar. It supports ordinary JSON,
    /// Gemma-delimited strings, unquoted primitive values, nested arrays/objects, and the degraded
    /// unquoted multiline strings observed from Darkbloom (notably execute_csharp code).
    /// </summary>
    private sealed class GemmaArgumentParser
    {
        private const string GemmaQuote = "<|\"|>";

        private readonly string text;
        private int position;

        private GemmaArgumentParser(string text) => this.text = text;

        internal static bool TryParse(string body, JObject schema, out JObject? result, out string? error)
        {
            result = null;
            error = null;

            // Prefer strict JSON when the gateway emitted ordinary OpenAI-style arguments inside
            // Gemma's outer call envelope.
            try
            {
                result = JObject.Parse("{" + body + "}", new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                });
                return true;
            }
            catch (JsonException)
            {
                // Continue with Gemma's native JSON-like grammar.
            }

            var parser = new GemmaArgumentParser(body);
            try
            {
                result = parser.ParseObjectBody(schema, enclosed: false);
                parser.SkipWhitespace();
                if (!parser.AtEnd)
                    throw parser.Error("unexpected text after the arguments object");
                return true;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool AtEnd => position >= text.Length;

        private JObject ParseObjectBody(JObject schema, bool enclosed)
        {
            if (enclosed) Expect('{');
            var result = new JObject();
            JObject properties = schema["properties"] as JObject ?? new JObject();

            SkipWhitespace();
            if (enclosed && Peek('}'))
            {
                position++;
                return result;
            }
            if (!enclosed && AtEnd) return result;

            while (true)
            {
                SkipWhitespace();
                string key = ParseKey();
                if (result.Property(key, StringComparison.Ordinal) != null)
                    throw Error($"duplicate argument '{key}'");

                SkipWhitespace();
                Expect(':');
                SkipWhitespace();

                JToken? propertySchema = properties[key];
                result[key] = ParseValue(propertySchema as JObject, properties,
                    captureRemainderForSoleString: !enclosed && properties.Properties().Count() <= 1,
                    commaAlwaysSeparates: false);

                SkipWhitespace();
                if (enclosed && Peek('}'))
                {
                    position++;
                    break;
                }
                if (!enclosed && AtEnd) break;

                Expect(',');
                SkipWhitespace();
                if ((enclosed && Peek('}')) || (!enclosed && AtEnd))
                    throw Error("trailing comma in arguments object");
            }
            return result;
        }

        private JArray ParseArray(JObject? schema)
        {
            Expect('[');
            var result = new JArray();
            JObject? itemSchema = schema?["items"] as JObject;
            SkipWhitespace();
            if (Peek(']'))
            {
                position++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(itemSchema, new JObject(),
                    captureRemainderForSoleString: false, commaAlwaysSeparates: true));
                SkipWhitespace();
                if (Peek(']'))
                {
                    position++;
                    break;
                }
                Expect(',');
                SkipWhitespace();
                if (Peek(']')) throw Error("trailing comma in array");
            }
            return result;
        }

        private JToken ParseValue(JObject? schema, JObject siblingProperties,
            bool captureRemainderForSoleString, bool commaAlwaysSeparates)
        {
            SkipWhitespace();
            if (AtEnd) throw Error("missing argument value");

            if (StartsWith(GemmaQuote))
                return new JValue(ParseGemmaString());
            if (Peek('"') || Peek('\''))
                return new JValue(ParseQuotedString());
            if (Peek('{'))
                return ParseObjectBody(schema ?? new JObject(), enclosed: true);
            if (Peek('['))
                return ParseArray(schema);

            string raw = ReadBareValue(schema, siblingProperties,
                captureRemainderForSoleString, commaAlwaysSeparates).Trim();
            if (raw.Length == 0) throw Error("empty argument value");
            return ConvertBareValue(raw, schema);
        }

        private string ParseKey()
        {
            if (StartsWith(GemmaQuote)) return ParseGemmaString();
            if (Peek('"') || Peek('\'')) return ParseQuotedString();

            int start = position;
            while (!AtEnd && text[position] != ':' && !char.IsWhiteSpace(text[position])
                   && text[position] is not ',' and not '}' and not ']')
                position++;
            string key = text[start..position].Trim();
            if (key.Length == 0) throw Error("missing argument name");
            return key;
        }

        private string ParseGemmaString()
        {
            position += GemmaQuote.Length;
            int end = text.IndexOf(GemmaQuote, position, StringComparison.Ordinal);
            if (end < 0) throw Error("unterminated Gemma string delimiter");
            string value = text[position..end];
            position = end + GemmaQuote.Length;
            return value;
        }

        private string ParseQuotedString()
        {
            char quote = text[position++];
            var value = new StringBuilder();
            while (!AtEnd)
            {
                char c = text[position++];
                if (c == quote) return value.ToString();
                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                if (AtEnd) throw Error("unterminated string escape");
                char escaped = text[position++];
                value.Append(escaped switch
                {
                    '"' => '"',
                    '\'' => '\'',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => ParseUnicodeEscape(),
                    _ => escaped,
                });
            }
            throw Error("unterminated quoted string");
        }

        private char ParseUnicodeEscape()
        {
            if (position + 4 > text.Length) throw Error("incomplete unicode escape");
            string hex = text.Substring(position, 4);
            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                throw Error("invalid unicode escape");
            position += 4;
            return (char)code;
        }

        private string ReadBareValue(JObject? schema, JObject siblingProperties,
            bool captureRemainderForSoleString, bool commaAlwaysSeparates)
        {
            int start = position;

            // A sole string property is the degraded Darkbloom form used for multiline code and
            // prose. Capturing to the end avoids mistaking commas, labels, or braces inside code
            // for argument separators.
            if (SchemaAllows(schema, "string") && captureRemainderForSoleString)
            {
                position = text.Length;
                return text[start..];
            }

            int braces = 0, brackets = 0, parentheses = 0;
            char quote = '\0';
            bool escaped = false, lineComment = false, blockComment = false;

            while (!AtEnd)
            {
                char c = text[position];
                char next = position + 1 < text.Length ? text[position + 1] : '\0';

                if (lineComment)
                {
                    position++;
                    if (c is '\r' or '\n') lineComment = false;
                    continue;
                }
                if (blockComment)
                {
                    position++;
                    if (c == '*' && next == '/')
                    {
                        position++;
                        blockComment = false;
                    }
                    continue;
                }
                if (quote != '\0')
                {
                    position++;
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '/' && next == '/')
                {
                    lineComment = true;
                    position += 2;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    blockComment = true;
                    position += 2;
                    continue;
                }
                if (c is '"' or '\'')
                {
                    quote = c;
                    position++;
                    continue;
                }

                switch (c)
                {
                    case '{': braces++; position++; continue;
                    case '[': brackets++; position++; continue;
                    case '(': parentheses++; position++; continue;
                    case '}' when braces > 0: braces--; position++; continue;
                    case ']' when brackets > 0: brackets--; position++; continue;
                    case ')' when parentheses > 0: parentheses--; position++; continue;
                }

                bool topLevel = braces == 0 && brackets == 0 && parentheses == 0;
                if (topLevel && c is '}' or ']') break;
                if (topLevel && c == ',')
                {
                    if (commaAlwaysSeparates
                        || !SchemaAllows(schema, "string")
                        || LooksLikeFollowingProperty(position + 1, siblingProperties))
                        break;
                }
                position++;
            }
            return text[start..position];
        }

        private bool LooksLikeFollowingProperty(int at, JObject siblingProperties)
        {
            int cursor = at;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length) return false;

            string key;
            if (text[cursor] is '"' or '\'')
            {
                char quote = text[cursor++];
                int start = cursor;
                while (cursor < text.Length && text[cursor] != quote) cursor++;
                if (cursor >= text.Length) return false;
                key = text[start..cursor++];
            }
            else
            {
                int start = cursor;
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor])
                       || text[cursor] is '_' or '-' or '.'))
                    cursor++;
                key = text[start..cursor];
            }

            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            return key.Length > 0 && cursor < text.Length && text[cursor] == ':'
                && siblingProperties.Property(key, StringComparison.Ordinal) != null;
        }

        private static JToken ConvertBareValue(string raw, JObject? schema)
        {
            if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return JValue.CreateNull();
            if (SchemaAllows(schema, "string")) return new JValue(raw);
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return new JValue(true);
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return new JValue(false);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                return new JValue(integer);
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return new JValue(number);
            return new JValue(raw);
        }

        private static bool SchemaAllows(JObject? schema, string type)
        {
            if (schema?["type"] is JValue scalar)
                return string.Equals(scalar.Value<string>(), type, StringComparison.OrdinalIgnoreCase);
            if (schema?["type"] is JArray choices)
                return choices.Values<string>().Any(x =>
                    string.Equals(x, type, StringComparison.OrdinalIgnoreCase));
            return false;
        }

        private bool StartsWith(string value) =>
            position + value.Length <= text.Length
            && text.AsSpan(position, value.Length).SequenceEqual(value.AsSpan());

        private bool Peek(char value) => !AtEnd && text[position] == value;

        private void Expect(char value)
        {
            if (AtEnd || text[position] != value)
                throw Error($"expected '{value}'");
            position++;
        }

        private void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(text[position])) position++;
        }

        private FormatException Error(string message) =>
            new($"{message} at character {position}.");
    }
}
