using System.Text;
using System.Text.RegularExpressions;

namespace ClayBeso.Tests;

/// <summary>
/// Generates docs/COMPONENTS.md from the Grasshopper component sources, so the reference can't drift
/// from the code. The test fails when the file is out of date. Regenerate with:
///   PowerShell:  $env:UPDATE_DOCS=1; dotnet test tests/ClayBeso.Tests --filter ComponentDocs; Remove-Item env:UPDATE_DOCS
///   Bash:        UPDATE_DOCS=1 dotnet test tests/ClayBeso.Tests --filter ComponentDocs
/// </summary>
public class ComponentDocsTests
{
    [Fact]
    public void ComponentReferenceIsUpToDate()
    {
        string root = RepoRoot();
        string generated = Generate(root);
        string path = Path.Combine(root, "docs", "COMPONENTS.md");

        if (Environment.GetEnvironmentVariable("UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, generated, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), "docs/COMPONENTS.md is missing. Regenerate it (see ComponentDocsTests).");
        string current = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(current == generated,
            "docs/COMPONENTS.md is out of date with the component sources. Regenerate it: " +
            "set UPDATE_DOCS=1 and run `dotnet test tests/ClayBeso.Tests --filter ComponentDocs`.");
    }

    /// <summary>
    /// A legacy version (old input order, old ID) shares all its code with the current component, which
    /// reads inputs and writes outputs by NAME. So both must have exactly the same names.
    /// </summary>
    [Theory]
    [InlineData("BesoComponent", "BesoComponentV1")]
    [InlineData("ShellAnalysisComponent", "ShellAnalysisComponentV1")]
    public void LegacyVersionsHaveTheSameParameterNames(string current, string legacy)
    {
        string dir = Path.Combine(RepoRoot(), "src", "ClayBeso.GH", "Components");
        var tabs = ReadTabs(Path.Combine(RepoRoot(), "src", "ClayBeso.GH", "ClayBesoInfo.cs"));
        var a = ParseComponent(Path.Combine(dir, current + ".cs"), tabs);
        var b = ParseComponent(Path.Combine(dir, legacy + ".cs"), tabs);
        Assert.Equal(a.Inputs.Select(p => p.Name).OrderBy(n => n), b.Inputs.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(a.Outputs.Select(p => p.Name).OrderBy(n => n), b.Outputs.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("hidden", b.Exposure);
    }

    // ------------------------------------------------------------------ generator

    private sealed record Param(string Kind, string Name, string Nick, string Description, string Access, string Default, bool Optional);

    private sealed record Component(string Name, string Nick, string Description, string Tab, string Exposure,
        List<Param> Inputs, List<Param> Outputs, string File);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClayBeso.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ClayBeso.sln not found above the test binaries.");
    }

    private static readonly string[] ExposureOrder = { "primary", "secondary", "tertiary", "quarternary", "quinary" };

    internal static string Generate(string root)
    {
        var tabs = ReadTabs(Path.Combine(root, "src", "ClayBeso.GH", "ClayBesoInfo.cs"));
        var components = Directory.GetFiles(Path.Combine(root, "src", "ClayBeso.GH", "Components"), "*Component.cs")
            .Select(f => ParseComponent(f, tabs))
            .OrderBy(c => c.Tab, StringComparer.Ordinal)
            .ThenBy(c => Array.IndexOf(ExposureOrder, c.Exposure))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("# Component reference\n\n");
        sb.Append("Every ClayBESO Grasshopper component with its inputs and outputs, generated from the source code by\n");
        sb.Append("`tests/ClayBeso.Tests/ComponentDocsTests.cs`. **Don't edit this file by hand**: change the component's\n");
        sb.Append("descriptions in code, then regenerate (`UPDATE_DOCS=1 dotnet test tests/ClayBeso.Tests --filter ComponentDocs`).\n");
        sb.Append("The test suite fails while this file is out of date.\n\n");
        sb.Append("Units: lengths of geometry follow the Rhino document; thickness, layer height and bead width are in mm,\n");
        sb.Append("forces in N, stresses in MPa. *Generic* inputs carry ClayBESO objects (a Shell Model, a Clay Material,\n");
        sb.Append("a Layer Rule, a Design from Shell BESO, a Print from Print Layers) from the component that makes them.\n");
        sb.Append("Per-face lists follow the faces of the triangulated\n");
        sb.Append("mesh output by Shell Model; per-vertex lists follow its vertices.\n\n");
        sb.Append("Inputs in **bold** must be connected. The others have a default (shown) or are optional. New inputs\n");
        sb.Append("are only ever added at the end, so saved definitions keep working.\n\n");

        foreach (var group in components.GroupBy(c => c.Tab))
        {
            sb.Append($"- **{group.Key}**: ");
            sb.Append(string.Join(", ", group.Select(c => $"[{c.Name}](#{Anchor(c.Name)})")));
            sb.Append('\n');
        }
        sb.Append('\n');

        foreach (var group in components.GroupBy(c => c.Tab))
        {
            sb.Append($"## {group.Key}\n\n");
            foreach (var c in group)
            {
                sb.Append($"### {c.Name}\n\n");
                sb.Append($"Nickname `{c.Nick}` · source `src/ClayBeso.GH/Components/{c.File}`\n\n");
                foreach (var para in c.Description.Split('\n').Where(p => p.Trim().Length > 0))
                    sb.Append(para.Trim()).Append("\n\n");

                sb.Append("**Inputs**\n\n");
                AppendTable(sb, c.Inputs, inputs: true);
                sb.Append("**Outputs**\n\n");
                AppendTable(sb, c.Outputs, inputs: false);
            }
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static string Anchor(string heading) =>
        Regex.Replace(heading.ToLowerInvariant(), @"[^a-z0-9 \-]", "").Replace(' ', '-');

    private static void AppendTable(StringBuilder sb, List<Param> ps, bool inputs)
    {
        if (ps.Count == 0) { sb.Append("None.\n\n"); return; }
        sb.Append(inputs
            ? "| # | Name | Nick | Type | Access | Default | Description |\n| --- | --- | --- | --- | --- | --- | --- |\n"
            : "| # | Name | Nick | Type | Access | Description |\n| --- | --- | --- | --- | --- | --- |\n");
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            bool required = inputs && !p.Optional && p.Default.Length == 0;
            string name = required ? $"**{Cell(p.Name)}**" : Cell(p.Name);
            string def = inputs ? (p.Default.Length > 0 ? $"`{Cell(p.Default)}`" : (p.Optional ? "optional" : "required")) : "";
            sb.Append(inputs
                ? $"| {i} | {name} | `{Cell(p.Nick)}` | {p.Kind} | {p.Access} | {def} | {Cell(p.Description)} |\n"
                : $"| {i} | {name} | `{Cell(p.Nick)}` | {p.Kind} | {p.Access} | {Cell(p.Description)} |\n");
        }
        sb.Append('\n');
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ").Trim();

    private static Dictionary<string, string> ReadTabs(string infoFile)
    {
        var src = File.ReadAllText(infoFile);
        return Regex.Matches(src, @"public const string (\w+) = ""([^""]*)"";")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static Component ParseComponent(string file, Dictionary<string, string> tabs)
    {
        string src = File.ReadAllText(file);
        int b = src.IndexOf(": base(", StringComparison.Ordinal);
        var baseArgs = SplitArgs(src, src.IndexOf('(', b));
        string tab = baseArgs.Count > 4 && baseArgs[4].Trim().StartsWith("Tab.")
            ? tabs[baseArgs[4].Trim().Substring(4)]
            : "Other";
        var exposure = Regex.Match(src, @"GH_Exposure\.(\w+)");
        // Hidden components are kept only so older definitions open: list them apart, last.
        if (exposure.Success && exposure.Groups[1].Value == "hidden") tab = "Legacy (hidden from the toolbar)";

        return new Component(
            EvalString(baseArgs[0]), EvalString(baseArgs[1]), EvalString(baseArgs[2]), tab,
            exposure.Success ? exposure.Groups[1].Value : "primary",
            ParseParams(MethodBody(src, "RegisterInputParams"), inputs: true),
            ParseParams(MethodBody(src, "RegisterOutputParams"), inputs: false),
            Path.GetFileName(file));
    }

    private static List<Param> ParseParams(string body, bool inputs)
    {
        var result = new List<(int pos, Param p)>();
        foreach (Match m in Regex.Matches(body, @"p\.Add(\w+)Parameter\("))
        {
            var args = SplitArgs(body, m.Index + m.Length - 1);
            string access = Regex.Match(args.Count > 3 ? args[3] : "", @"GH_ParamAccess\.(\w+)").Groups[1].Value;
            string def = args.Count > 4 ? args[4].Trim() : "";
            def = Regex.Replace(def, @"^new Vector3d(\(.*\))$", "$1");
            result.Add((m.Index, new Param(Kind(m.Groups[1].Value), EvalString(args[0]), EvalString(args[1]),
                EvalString(args[2]), access, def, false)));
        }

        var optional = new bool[result.Count];
        if (inputs)
        {
            foreach (Match m in Regex.Matches(body, @"for \(int i = (\d+); i < p\.ParamCount; i\+\+\) p\[i\]\.Optional = true;"))
            {
                int from = int.Parse(m.Groups[1].Value);
                int before = result.Count(r => r.pos < m.Index);
                for (int i = from; i < before; i++) optional[i] = true;
            }
            foreach (Match m in Regex.Matches(body, @"p\[(\d+)\]\.Optional = true;"))
                optional[int.Parse(m.Groups[1].Value)] = true;
        }
        return result.Select((r, i) => r.p with { Optional = optional[i] }).ToList();
    }

    private static string Kind(string method) => method switch
    {
        "Number" => "Number",
        "Integer" => "Integer",
        "Boolean" => "Boolean",
        "Generic" => "Generic",
        "Text" => "Text",
        _ => method,
    };

    /// <summary>Text between the braces of the first method with this name.</summary>
    private static string MethodBody(string src, string name)
    {
        int i = src.IndexOf(name + "(", StringComparison.Ordinal);
        int open = src.IndexOf('{', i);
        int depth = 0;
        bool inString = false;
        for (int k = open; k < src.Length; k++)
        {
            char c = src[k];
            if (c == '"' && src[k - 1] != '\\') inString = !inString;
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return src.Substring(open + 1, k - open - 1);
        }
        throw new FormatException($"Unbalanced braces in {name}.");
    }

    /// <summary>Top-level comma-separated arguments of the call whose '(' is at <paramref name="open"/>.</summary>
    private static List<string> SplitArgs(string src, int open)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        int depth = 0;
        bool inString = false;
        for (int k = open + 1; k < src.Length; k++)
        {
            char c = src[k];
            if (c == '"' && src[k - 1] != '\\') inString = !inString;
            if (!inString)
            {
                if (c == '(' || c == '{' || c == '[') depth++;
                else if (c == ')' || c == '}' || c == ']')
                {
                    if (depth == 0) { args.Add(cur.ToString()); return args; }
                    depth--;
                }
                else if (c == ',' && depth == 0) { args.Add(cur.ToString()); cur.Clear(); continue; }
            }
            cur.Append(c);
        }
        throw new FormatException("Unterminated argument list.");
    }

    /// <summary>Concatenation of C# string literals ("a" + "b"), escapes resolved.</summary>
    private static string EvalString(string expr)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(expr, @"(?<!\$)""((?:[^""\\]|\\.)*)"""))
            sb.Append(Regex.Unescape(m.Groups[1].Value));
        return sb.ToString();
    }
}
