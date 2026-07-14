using System;
using System.Text;
using BBT.Aether.Uow.EntityFrameworkCore;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Finds raw schema tokens in PostgreSQL code while preserving tokens in lexical regions
/// where PostgreSQL treats their text as data.
/// </summary>
internal static class PostgreSqlRawSchemaTokenRewriter
{
    // EF Core applies composite formatting before command interception, so the public
    // {{schema}} token arrives as {schema} whenever the raw SQL call has parameters.
    private const string FormattedRawSqlToken = "{schema}";

    public static RewriteResult Rewrite(string commandText, string? replacement)
    {
        var rewritten = new StringBuilder(commandText.Length);
        var foundToken = false;
        var position = 0;

        while (position < commandText.Length)
        {
            if (commandText[position] == '\'' || commandText[position] == '"')
            {
                CopyQuotedRegion(commandText, rewritten, ref position, commandText[position]);
                continue;
            }

            if (StartsWith(commandText, position, "--"))
            {
                CopyLineComment(commandText, rewritten, ref position);
                continue;
            }

            if (StartsWith(commandText, position, "/*"))
            {
                CopyBlockComment(commandText, rewritten, ref position);
                continue;
            }

            if (commandText[position] == '$' &&
                TryReadDollarQuoteDelimiter(commandText, position, out var delimiter))
            {
                CopyDollarQuotedRegion(commandText, rewritten, ref position, delimiter);
                continue;
            }

            var tokenLength = GetTokenLength(commandText, position);
            if (tokenLength > 0)
            {
                foundToken = true;
                if (replacement is null)
                    rewritten.Append(commandText, position, tokenLength);
                else
                    rewritten.Append(replacement);
                position += tokenLength;
                continue;
            }

            rewritten.Append(commandText[position]);
            position++;
        }

        return new RewriteResult(rewritten.ToString(), foundToken);
    }

    private static int GetTokenLength(string sql, int position)
    {
        if (StartsWith(sql, position, AetherSchemaModel.RawSqlToken))
            return AetherSchemaModel.RawSqlToken.Length;
        if (StartsWith(sql, position, FormattedRawSqlToken))
            return FormattedRawSqlToken.Length;
        return 0;
    }

    private static void CopyQuotedRegion(
        string sql,
        StringBuilder output,
        ref int position,
        char quote)
    {
        output.Append(sql[position++]);
        while (position < sql.Length)
        {
            var current = sql[position++];
            output.Append(current);
            if (quote == '\'' && current == '\\' && position < sql.Length)
            {
                output.Append(sql[position++]);
                continue;
            }

            if (current != quote)
                continue;

            if (position < sql.Length && sql[position] == quote)
            {
                output.Append(sql[position++]);
                continue;
            }

            return;
        }
    }

    private static void CopyLineComment(string sql, StringBuilder output, ref int position)
    {
        while (position < sql.Length)
        {
            var current = sql[position++];
            output.Append(current);
            if (current == '\n')
                return;
        }
    }

    private static void CopyBlockComment(string sql, StringBuilder output, ref int position)
    {
        var depth = 0;
        while (position < sql.Length)
        {
            if (StartsWith(sql, position, "/*"))
            {
                output.Append("/*");
                position += 2;
                depth++;
                continue;
            }

            if (StartsWith(sql, position, "*/"))
            {
                output.Append("*/");
                position += 2;
                depth--;
                if (depth == 0)
                    return;
                continue;
            }

            output.Append(sql[position++]);
        }
    }

    private static bool TryReadDollarQuoteDelimiter(
        string sql,
        int position,
        out string delimiter)
    {
        delimiter = string.Empty;
        if (position > 0 && IsIdentifierContinuation(sql[position - 1]))
            return false;

        var cursor = position + 1;
        if (cursor < sql.Length && sql[cursor] == '$')
        {
            delimiter = "$$";
            return true;
        }

        if (cursor >= sql.Length || !IsTagStart(sql[cursor]))
            return false;

        cursor++;
        while (cursor < sql.Length && IsTagContinuation(sql[cursor]))
            cursor++;

        if (cursor >= sql.Length || sql[cursor] != '$')
            return false;

        delimiter = sql.Substring(position, cursor - position + 1);
        return true;
    }

    private static void CopyDollarQuotedRegion(
        string sql,
        StringBuilder output,
        ref int position,
        string delimiter)
    {
        var closingPosition = sql.IndexOf(
            delimiter,
            position + delimiter.Length,
            StringComparison.Ordinal);
        if (closingPosition < 0)
        {
            output.Append(sql, position, sql.Length - position);
            position = sql.Length;
            return;
        }

        var length = closingPosition + delimiter.Length - position;
        output.Append(sql, position, length);
        position += length;
    }

    private static bool StartsWith(string value, int position, string candidate) =>
        position <= value.Length - candidate.Length &&
        value.AsSpan(position, candidate.Length).SequenceEqual(candidate.AsSpan());

    private static bool IsIdentifierContinuation(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

    private static bool IsTagStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsTagContinuation(char value) => char.IsLetterOrDigit(value) || value == '_';

    internal readonly record struct RewriteResult(string CommandText, bool FoundToken);
}
