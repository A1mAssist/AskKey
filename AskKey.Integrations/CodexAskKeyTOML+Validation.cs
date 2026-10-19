namespace AskKey.Integrations;

internal static partial class CodexAskKeyTOML
{
    public static void AssertAssignmentsAndKeyPaths(Section[] parts)
    {
        var tables = new HashSet<string>();
        var implicitTables = new HashSet<string>();
        var values = new HashSet<string>();
        var arrayTables = new HashSet<string>();
        foreach (var part in parts)
        {
            if (part.Name is { } name)
            {
                if (name.Parts.Length == 0 || name.Parts.Any(string.IsNullOrEmpty))
                {
                    throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                }
                if (part.IsArray)
                {
                    DefineArrayTable(name.Parts, arrayTables, tables, values, implicitTables);
                }
                else if (ArrayAncestor(name.Parts, arrayTables) is null)
                {
                    DefineTable(name.Parts, tables, values, arrayTables, implicitTables);
                }
            }
            var keysInSection = new HashSet<string>();
            var scan = Scan.Normal;
            foreach (var line in Lines(part.Raw, part.Newline))
            {
                if (scan.IsNormal)
                {
                    var code = CodePortion(line).Trim();
                    if (KeyName(code) is { } key)
                    {
                        var local = DottedParts(key);
                        if (local is null || local.Length == 0 || local.Any(string.IsNullOrEmpty))
                        {
                            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                        }
                        if (!keysInSection.Add(Join(local)))
                        {
                            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                        }
                        AssertAssignmentHasValue(code);
                        var path = (part.Name?.Parts ?? Array.Empty<string>()).Concat(local).ToArray();
                        var underArray = part.IsArray
                            || ArrayAncestor(part.Name?.Parts ?? Array.Empty<string>(), arrayTables) is not null;
                        if (!underArray)
                        {
                            DefineValue(
                                path,
                                part.Name?.Parts.Length ?? 0,
                                tables, values, arrayTables, implicitTables);
                        }
                    }
                }
                scan = Advance(scan, line);
            }
        }
    }

    private static string Join(string[] path) => string.Join('\u001f', path);

    private static void AssertAssignmentHasValue(string code)
    {
        var equals = code.IndexOf('=');
        if (equals < 0) return;
        if (code[(equals + 1)..].Trim().Length == 0)
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
    }

    private static void DefineArrayTable(
        string[] path,
        HashSet<string> arrayTables,
        HashSet<string> tables,
        HashSet<string> values,
        HashSet<string> implicitTables)
    {
        if (path.Length == 0 || path.Any(string.IsNullOrEmpty))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        var key = Join(path);
        if (values.Contains(key) || tables.Contains(key) || implicitTables.Contains(key))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        if (!arrayTables.Add(key)) return;
        for (var index = 1; index < path.Length; index++)
        {
            var prefix = Join(path[..index]);
            if (values.Contains(prefix))
            {
                throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
            }
            if (tables.Contains(prefix) || arrayTables.Contains(prefix)) continue;
            implicitTables.Add(prefix);
        }
    }

    private static void DefineTable(
        string[] path,
        HashSet<string> tables,
        HashSet<string> values,
        HashSet<string> arrayTables,
        HashSet<string> implicitTables)
    {
        if (path.Length == 0 || path.Any(string.IsNullOrEmpty))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        var key = Join(path);
        if (values.Contains(key) || arrayTables.Contains(key))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        if (tables.Contains(key))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        for (var index = 1; index < path.Length; index++)
        {
            var prefix = Join(path[..index]);
            if (values.Contains(prefix) || arrayTables.Contains(prefix))
            {
                throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
            }
            if (!tables.Contains(prefix)) implicitTables.Add(prefix);
        }
        implicitTables.Remove(key);
        tables.Add(key);
    }

    private static void DefineValue(
        string[] path,
        int tableDepth,
        HashSet<string> tables,
        HashSet<string> values,
        HashSet<string> arrayTables,
        HashSet<string> implicitTables)
    {
        if (path.Length == 0 || path.Any(string.IsNullOrEmpty))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        var key = Join(path);
        if (tables.Contains(key) || values.Contains(key) || arrayTables.Contains(key)
            || implicitTables.Contains(key))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        for (var index = 1; index < path.Length; index++)
        {
            var prefix = Join(path[..index]);
            if (values.Contains(prefix) || arrayTables.Contains(prefix))
            {
                throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
            }
            // Dotted keys define their parent tables, unlike table-header ancestors.
            if (index > tableDepth)
            {
                implicitTables.Remove(prefix);
                tables.Add(prefix);
            }
        }
        values.Add(key);
    }

    private static string? ArrayAncestor(string[] path, HashSet<string> arrayTables)
    {
        for (var index = path.Length - 1; index >= 1; index--)
        {
            var prefix = Join(path[..index]);
            if (arrayTables.Contains(prefix)) return prefix;
        }
        return null;
    }
}
