using System.Text;
using System.Text.Json;

namespace Yamv.Core.Theming;

/// <summary>
/// Edits a theme file's text so it holds a new definition, changing only what differs from the definition the text holds now: a
/// changed value is replaced where it stands, an added field or token is inserted beside its neighbours with their indentation, and a
/// removed one is cut out with its comma. Comments, field order and unknown fields stay as they are.
/// </summary>
/// <remarks>
/// Every edit re-reads the text, so each one works on fresh positions. The caller checks that the result reads back as the
/// definition wanted, since an unusual layout (a comment holding a line break where an insertion goes) can defeat an edit.
/// </remarks>
internal sealed class ThemeFilePatch
{
    private static readonly string[] RootOrder = ["$schema", "formatVersion", "name", "author", "description", "tokens", "css", "light", "dark"];
    private static readonly string[] VariantOrder = ["tokens", "css"];
    private static readonly string[] TokenOrder = [.. ThemeTokens.All.Select(token => token.Name)];
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _path;
    private readonly string _newLine;
    private byte[] _bytes;

    private ThemeFilePatch(string text, string path)
    {
        _bytes = Encoding.UTF8.GetBytes(text);
        _path = path;
        _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private delegate string ValueText(string indent);

    /// <summary>
    /// <paramref name="text"/>, the text of the file at <paramref name="path"/>, edited from <paramref name="onDisk"/> to
    /// <paramref name="wanted"/>.
    /// </summary>
    /// <exception cref="ThemeException">The text is not a JSON object, or a key being edited, or a section on its way, appears twice.</exception>
    public static string Apply(string text, string path, ThemeDefinition onDisk, ThemeDefinition wanted)
    {
        var patch = new ThemeFilePatch(text, path);
        patch.SetString([], "name", onDisk.Name, wanted.Name);
        patch.SetString([], "author", onDisk.Author, wanted.Author);
        patch.SetString([], "description", onDisk.Description, wanted.Description);
        patch.SetTokens([], onDisk.SharedTokens, wanted.SharedTokens);
        patch.SetString([], "css", NullIfEmpty(onDisk.SharedCss), NullIfEmpty(wanted.SharedCss));
        patch.SetVariant("light", onDisk.Light, wanted.Light);
        patch.SetVariant("dark", onDisk.Dark, wanted.Dark);
        return Encoding.UTF8.GetString(patch._bytes);
    }

    /// <summary>The exception for a file that cannot be edited in place, leaving it as it is.</summary>
    public static ThemeException CannotUpdate(string path, string reason)
    {
        string error = CannotUpdateError(path, reason);
        return new ThemeException(error, [error]);
    }

    private static ThemeException CannotUpdate(string path, Exception inner)
    {
        string error = CannotUpdateError(path, inner.Message);
        return new ThemeException(error, [error], inner);
    }

    private static string CannotUpdateError(string path, string reason) => $"The theme file {path} could not be updated in place: {reason}";

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static string[] OrderFor(string[] path) =>
        path.Length == 0 ? RootOrder
        : path[^1] == "tokens" ? TokenOrder
        : VariantOrder;

    private void SetString(string[] path, string name, string? old, string? wanted)
    {
        if (old != wanted)
        {
            SetProperty(path, name, wanted is null ? null : _ => ThemeSerializer.StringText(wanted));
        }
    }

    private void SetVariant(string name, ThemeVariant? old, ThemeVariant? wanted)
    {
        if (wanted is null || old is null)
        {
            if (old is not null || wanted is not null)
            {
                SetProperty([], name, wanted is null ? null : indent => VariantText(wanted, indent));
            }

            return;
        }

        SetTokens([name], old.Tokens, wanted.Tokens);
        SetString([name], "css", NullIfEmpty(old.Css), NullIfEmpty(wanted.Css));
    }

    private void SetTokens(string[] path, IReadOnlyDictionary<string, ThemeValue> old, IReadOnlyDictionary<string, ThemeValue> wanted)
    {
        if (ThemeDefinition.SameTokens(old, wanted))
        {
            return;
        }

        string[] tokensPath = [.. path, "tokens"];
        if (FindObject(Root(), tokensPath) is null)
        {
            SetProperty(path, "tokens", indent => TokensText(wanted, indent));
            return;
        }

        foreach (string name in old.Keys.Where(name => !wanted.ContainsKey(name)).ToList())
        {
            SetProperty(tokensPath, name, null);
        }

        foreach (
            (string name, ThemeValue value) in wanted
                .OrderBy(pair => OrderIndex(TokenOrder, pair.Key))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
        )
        {
            if (!old.TryGetValue(name, out ThemeValue? before) || !Equals(before, value))
            {
                SetProperty(tokensPath, name, _ => ThemeSerializer.ValueText(value));
            }
        }
    }

    private string VariantText(ThemeVariant variant, string indent)
    {
        var fields = new List<(string Name, ValueText Value)>();
        if (variant.Tokens.Count > 0)
        {
            fields.Add(("tokens", inner => TokensText(variant.Tokens, inner)));
        }

        if (variant.Css.Length > 0)
        {
            fields.Add(("css", _ => ThemeSerializer.StringText(variant.Css)));
        }

        return ObjectText(fields, indent);
    }

    private string TokensText(IReadOnlyDictionary<string, ThemeValue> tokens, string indent) =>
        ObjectText(
            [
                .. tokens
                    .OrderBy(pair => OrderIndex(TokenOrder, pair.Key))
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => (pair.Key, (ValueText)(_ => ThemeSerializer.ValueText(pair.Value)))),
            ],
            indent
        );

    /// <summary>A JSON object holding <paramref name="fields"/>, laid out as the file lays out its objects.</summary>
    private string ObjectText(List<(string Name, ValueText Value)> fields, string indent)
    {
        if (fields.Count == 0)
        {
            return "{}";
        }

        if (!IsMultiline())
        {
            return "{ " + string.Join(", ", fields.Select(field => PropertyText(field.Name, field.Value, indent))) + " }";
        }

        string inner = indent + IndentUnit();
        IEnumerable<string> lines = fields.Select(field => inner + PropertyText(field.Name, field.Value, inner));
        return "{" + _newLine + string.Join("," + _newLine, lines) + _newLine + indent + "}";
    }

    private static string PropertyText(string name, ValueText value, string indent) => ThemeSerializer.StringText(name) + ": " + value(indent);

    /// <summary>
    /// Sets property <paramref name="name"/> of the object at <paramref name="path"/>, or removes it when <paramref name="value"/>
    /// is null.
    /// </summary>
    private void SetProperty(string[] path, string name, ValueText? value)
    {
        JsonObject target = FindObject(Root(), path) ?? throw CannotUpdate(_path, $"its '{string.Join(".", path)}' is not an object.");
        EnsureOnce(target, name, path.Length == 0 ? "the top level" : $"'{string.Join(".", path)}'");
        int index = target.Properties.FindIndex(property => property.Name == name);
        if (value is null)
        {
            if (index >= 0)
            {
                Remove(target, index);
            }

            return;
        }

        if (index >= 0)
        {
            JsonProperty property = target.Properties[index];
            Edit((property.ValueStart, property.ValueEnd, value(IndentOf(property.NameStart))));
            return;
        }

        Insert(target, name, value, OrderFor(path));
    }

    private void Insert(JsonObject target, string name, ValueText value, string[] order)
    {
        int rank = OrderIndex(order, name);
        JsonProperty? anchor = target
            .Properties.Where(property => OrderIndex(order, property.Name) < rank)
            .MaxBy(property => OrderIndex(order, property.Name));
        bool multiline = target.Properties.Count > 0 ? IsOwnLine(target.Properties[0].NameStart) : IsMultiline();
        string indent = target.Properties.Count > 0 && multiline ? IndentOf(target.Properties[0].NameStart) : IndentOf(target.Start) + IndentUnit();
        string property = PropertyText(name, value, indent);

        if (target.Properties.Count == 0)
        {
            InsertIntoEmpty(target, property, indent, multiline);
        }
        else if (anchor is null)
        {
            JsonProperty first = target.Properties[0];
            int lineEnd = LineEnd(target.Start + 1);
            Edit(
                multiline && lineEnd < first.NameStart
                    ? (lineEnd, lineEnd, _newLine + indent + property + ",")
                    : (first.NameStart, first.NameStart, property + ", ")
            );
        }
        else
        {
            InsertAfter(target, anchor, property, indent, multiline);
        }
    }

    private void InsertIntoEmpty(JsonObject target, string property, string indent, bool multiline)
    {
        bool blank = _bytes.AsSpan(target.Start + 1, target.End - target.Start - 2).Trim(" \t\r\n"u8).IsEmpty;
        if (!blank)
        {
            Edit((target.Start + 1, target.Start + 1, multiline ? _newLine + indent + property : " " + property));
        }
        else if (multiline)
        {
            Edit((target.Start, target.End, "{" + _newLine + indent + property + _newLine + IndentOf(target.Start) + "}"));
        }
        else
        {
            Edit((target.Start, target.End, "{ " + property + " }"));
        }
    }

    private void InsertAfter(JsonObject target, JsonProperty anchor, string property, string indent, bool multiline)
    {
        int comma = CommaAfter(anchor.ValueEnd);
        int next = target.Properties.IndexOf(anchor) + 1;
        int limit = next < target.Properties.Count ? target.Properties[next].NameStart : target.End - 1;
        int lineEnd = LineEnd(comma >= 0 ? comma + 1 : anchor.ValueEnd);
        if (!multiline || lineEnd >= limit)
        {
            Edit((anchor.ValueEnd, anchor.ValueEnd, ", " + property));
        }
        else if (comma >= 0)
        {
            Edit((lineEnd, lineEnd, _newLine + indent + property + ","));
        }
        else
        {
            Edit((lineEnd, lineEnd, _newLine + indent + property), (anchor.ValueEnd, anchor.ValueEnd, ","));
        }
    }

    /// <summary>Removes property <paramref name="index"/> of <paramref name="target"/> with its comma, keeping the comments around it.</summary>
    private void Remove(JsonObject target, int index)
    {
        JsonProperty property = target.Properties[index];
        int comma = CommaAfter(property.ValueEnd);
        if (comma >= 0)
        {
            Edit(RemovalSpan(property.NameStart, comma + 1));
        }
        else if (index > 0)
        {
            int previousComma = CommaAfter(target.Properties[index - 1].ValueEnd);
            Edit(RemovalSpan(property.NameStart, property.ValueEnd), (previousComma, previousComma + 1, ""));
        }
        else
        {
            Edit(RemovalSpan(property.NameStart, property.ValueEnd));
        }
    }

    /// <summary>The span to cut for <paramref name="start"/>..<paramref name="end"/>: its whole line when nothing else is on it.</summary>
    private (int Start, int End, string Text) RemovalSpan(int start, int end)
    {
        int lineEnd = LineEnd(end);
        bool restBlank = _bytes.AsSpan(end, lineEnd - end).Trim(" \t"u8).IsEmpty;
        if (IsOwnLine(start) && restBlank && lineEnd < _bytes.Length)
        {
            int lineStart = LineStart(start);
            int afterBreak = _bytes[lineEnd] == '\r' ? lineEnd + 2 : lineEnd + 1;
            return (lineStart, afterBreak, "");
        }

        int stop = end;
        while (stop < _bytes.Length && _bytes[stop] is (byte)' ' or (byte)'\t')
        {
            stop++;
        }

        return (start, stop, "");
    }

    /// <summary>Applies edits given in descending order of position, so each leaves the positions of the next valid.</summary>
    private void Edit(params (int Start, int End, string Text)[] edits)
    {
        byte[] bytes = _bytes;
        foreach ((int start, int end, string text) in edits)
        {
            bytes = [.. bytes.AsSpan(0, start), .. Encoding.UTF8.GetBytes(text), .. bytes.AsSpan(end)];
        }

        _bytes = bytes;
    }

    /// <summary>The position of the comma after <paramref name="from"/>, skipping white space and comments, or -1 when none comes next.</summary>
    private int CommaAfter(int from)
    {
        int index = SkipTrivia(from);
        return index < _bytes.Length && _bytes[index] == ',' ? index : -1;
    }

    /// <summary>The position of the first byte at or after <paramref name="from"/> that is not white space or inside a comment.</summary>
    private int SkipTrivia(int from)
    {
        int index = from;
        while (index < _bytes.Length)
        {
            ReadOnlySpan<byte> rest = _bytes.AsSpan(index);
            if (rest[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                index++;
            }
            else if (rest.StartsWith("//"u8))
            {
                index = LineEnd(index);
            }
            else if (rest.StartsWith("/*"u8))
            {
                int close = rest[2..].IndexOf("*/"u8);
                index = close < 0 ? _bytes.Length : index + 2 + close + 2;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>The position of the line break (its <c>\r</c> in a CRLF file) that ends the line holding <paramref name="position"/>.</summary>
    private int LineEnd(int position)
    {
        int offset = _bytes.AsSpan(position).IndexOf((byte)'\n');
        if (offset < 0)
        {
            return _bytes.Length;
        }

        int end = position + offset;
        return end > position && _bytes[end - 1] == '\r' ? end - 1 : end;
    }

    private int LineStart(int position) => _bytes.AsSpan(0, position).LastIndexOf((byte)'\n') + 1;

    private bool IsOwnLine(int position) => _bytes.AsSpan(LineStart(position), position - LineStart(position)).Trim(" \t"u8).IsEmpty;

    private string IndentOf(int position)
    {
        int start = LineStart(position);
        int end = start;
        while (end < _bytes.Length && _bytes[end] is (byte)' ' or (byte)'\t')
        {
            end++;
        }

        return Encoding.UTF8.GetString(_bytes, start, end - start);
    }

    /// <summary>Whether the file puts its top-level fields on lines of their own.</summary>
    private bool IsMultiline()
    {
        JsonObject root = Root();
        return root.Properties.Count > 0 ? IsOwnLine(root.Properties[0].NameStart) : _bytes.AsSpan().Contains((byte)'\n');
    }

    /// <summary>One level of indentation: that of the file's first top-level field, or two spaces.</summary>
    private string IndentUnit()
    {
        JsonObject root = Root();
        string indent = root.Properties.Count > 0 && IsOwnLine(root.Properties[0].NameStart) ? IndentOf(root.Properties[0].NameStart) : "";
        return indent.Length > 0 ? indent : "  ";
    }

    private static int OrderIndex(string[] order, string name)
    {
        int index = Array.IndexOf(order, name);
        return index < 0 ? int.MaxValue : index;
    }

    private JsonObject? FindObject(JsonObject root, string[] path)
    {
        JsonObject current = root;
        string where = "the top level";
        foreach (string segment in path)
        {
            EnsureOnce(current, segment, where);
            JsonObject? next = current.Properties.Find(property => property.Name == segment)?.Object;
            if (next is null)
            {
                return null;
            }

            current = next;
            where = $"'{segment}'";
        }

        return current;
    }

    /// <summary>Refuses an edit through a key the object holds twice; other repeated keys, such as two comment fields, are left alone.</summary>
    private void EnsureOnce(JsonObject target, string name, string where)
    {
        if (target.Properties.Count(property => property.Name == name) > 1)
        {
            throw CannotUpdate(_path, $"'{name}' appears twice in {where}.");
        }
    }

    private JsonObject Root()
    {
        try
        {
            var reader = new Utf8JsonReader(_bytes, ReaderOptions);
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject
                ? ReadObject(ref reader)
                : throw CannotUpdate(_path, "it does not hold a JSON object.");
        }
        catch (JsonException exception)
        {
            throw CannotUpdate(_path, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw CannotUpdate(_path, exception);
        }
    }

    private static JsonObject ReadObject(ref Utf8JsonReader reader)
    {
        int start = (int)reader.TokenStartIndex;
        var properties = new List<JsonProperty>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString()!;
            int nameStart = (int)reader.TokenStartIndex;
            reader.Read();
            int valueStart = (int)reader.TokenStartIndex;
            JsonObject? value = null;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                value = ReadObject(ref reader);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            properties.Add(new JsonProperty(name, nameStart, valueStart, (int)reader.BytesConsumed, value));
        }

        return new JsonObject(start, (int)reader.BytesConsumed, properties);
    }

    /// <summary>A JSON object's span, from its <c>{</c> to just after its <c>}</c>, and its properties in file order.</summary>
    private sealed record JsonObject(int Start, int End, List<JsonProperty> Properties);

    /// <summary>A property's name position and value span; <paramref name="Object"/> is the value when it is an object.</summary>
    private sealed record JsonProperty(string Name, int NameStart, int ValueStart, int ValueEnd, JsonObject? Object);
}
