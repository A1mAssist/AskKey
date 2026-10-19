using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

return await BuildTool.RunAsync(args);

internal static partial class BuildTool
{
    private const string Runtime = "win-x64";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "manifest" => RunManifest(args[1..]),
                "publish" => await RunPublishAsync(args[1..]),
                "guard" => await RunGuardAsync(args[1..]),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static int RunManifest(string[] args)
    {
        var root = RequiredOption(args, "--root");
        var output = RequiredOption(args, "--output");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(rootFull))
            throw new DirectoryNotFoundException($"Helper root does not exist: {rootFull}");

        var prefix = rootFull + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(rootFull, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Helper payload contains a reparse point: {directory}");
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Helper payload contains a reparse point: {file}");

            var full = Path.GetFullPath(file);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Helper file escaped the root: {full}");

            var relative = Path.GetRelativePath(rootFull, full).Replace('\\', '/');
            using var stream = File.OpenRead(full);
            files[relative] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        if (files.Count == 0)
            throw new InvalidOperationException($"Helper root contains no files: {rootFull}");

        var outputFull = Path.GetFullPath(output);
        var parent = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var json = JsonSerializer.Serialize(
            new { version = 1, files },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputFull, json, new UTF8Encoding(false));
        return 0;
    }

    private static async Task<int> RunPublishAsync(string[] args)
    {
        var root = FindRepoRoot();
        var output = GetOption(args, "--output") ?? Path.Combine(root, "artifacts", "publish");
        var certificate = GetOption(args, "--certificate-thumbprint");
        var timestampUrl = GetOption(args, "--timestamp-url") ?? "http://timestamp.digicert.com";
        var skipTests = args.Contains("--skip-tests", StringComparer.OrdinalIgnoreCase);

        var solution = Path.Combine(root, "AskKey.slnx");
        var project = Path.Combine(root, "AskKey.WinUI", "AskKey.WinUI.csproj");
        var versionSource = Path.Combine(root, "AskKey.Integrations", "AskKeyVersion.cs");
        if (!File.Exists(solution)) throw new FileNotFoundException("AskKey.slnx was not found.", solution);
        if (!File.Exists(project)) throw new FileNotFoundException("AskKey.WinUI.csproj was not found.", project);
        if (!File.Exists(versionSource)) throw new FileNotFoundException("AskKeyVersion.cs was not found.", versionSource);

        var versionMatch = VersionPattern().Match(File.ReadAllText(versionSource));
        if (!versionMatch.Success)
            throw new InvalidOperationException("Could not read AskKeyVersion.Current.");
        var version = versionMatch.Groups["version"].Value;

        var publishDir = Path.Combine(output, $"AskKey-{version}-{Runtime}");
        var releaseDir = Path.Combine(output, "release");
        if (Directory.Exists(publishDir))
            throw new InvalidOperationException($"Publish directory already exists: {publishDir}");

        if (!skipTests)
            Run("dotnet", ["test", solution, "-c", "Release", "--nologo", "-v", "q"], root);

        var unsigned = string.IsNullOrWhiteSpace(certificate);
        var buildArgs = new List<string>
        {
            "build", project, "-c", "Release", "-p:Platform=x64", $"-p:Version={version}", "--nologo", "-v", "minimal"
        };
        if (unsigned) buildArgs.Add("-p:AskKeyUnsigned=true");
        Run("dotnet", buildArgs, root);

        var buildRoot = Path.Combine(root, "AskKey.WinUI", "bin", "x64", "Release");
        var buildDir = Directory.EnumerateDirectories(buildRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "AskKey.exe"))
                && File.Exists(Path.Combine(directory, "App.xbf")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new DirectoryNotFoundException($"Build output was not found under {buildRoot}.");

        Directory.CreateDirectory(publishDir);
        CopyDirectory(buildDir, publishDir);

        foreach (var directory in Directory.EnumerateDirectories(publishDir))
        {
            if (!string.Equals(Path.GetFileName(directory), "zh-CN", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(directory, "Microsoft.ui.xaml.dll.mui")))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var hostPath = Path.Combine(publishDir, "AskKey.exe");
        var helperPath = Path.Combine(publishDir, "Helpers", "askkey.exe");
        foreach (var required in new[]
                 {
                     hostPath,
                     helperPath,
                     Path.Combine(publishDir, "App.xbf"),
                     Path.Combine(publishDir, "Views")
                 })
        {
            if (!File.Exists(required) && !Directory.Exists(required))
                throw new FileNotFoundException($"The published layout is missing {required}.");
        }

        if (!unsigned)
        {
            var signtool = FindSigntool();
            foreach (var file in new[] { hostPath, helperPath })
            {
                Run(signtool,
                    ["sign", "/sha1", certificate!, "/fd", "SHA256", "/tr", timestampUrl, "/td", "SHA256", file],
                    root);
                Run(signtool, ["verify", "/pa", "/all", file], root);
            }
        }

        Directory.CreateDirectory(releaseDir);
        var packArgs = new List<string>
        {
            "pack", "--packId", "AskKey", "--packVersion", version, "--packDir", publishDir,
            "--packTitle", "Ask Key", "--packAuthors", "A1mAssist",
            "--mainExe", "AskKey.exe", "--outputDir", releaseDir,
            "--msi", "--instLocation", "PerMachine"
        };
        if (!unsigned)
        {
            packArgs.Add("--signParams");
            packArgs.Add($"/sha1 {certificate} /fd SHA256 /tr {timestampUrl} /td SHA256");
        }
        Run("vpk", packArgs, root);

        Console.WriteLine($"Published: {publishDir}");
        Console.WriteLine($"Release:   {releaseDir}");
        foreach (var file in Directory.EnumerateFiles(releaseDir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {Path.GetFileName(file)}");
        if (unsigned)
            Console.WriteLine("Unsigned release: SmartScreen will warn. Agent connections use the embedded helper integrity manifest.");
        return 0;
    }

    private static async Task<int> RunGuardAsync(string[] args)
    {
        var root = FindRepoRoot();
        var output = GetOption(args, "--output") ?? Path.Combine(root, "artifacts", "guard");
        var certificate = GetOption(args, "--certificate-thumbprint");
        var timestampUrl = GetOption(args, "--timestamp-url") ?? "http://timestamp.digicert.com";
        var skipTests = args.Contains("--skip-tests", StringComparer.OrdinalIgnoreCase);

        var solution = Path.Combine(root, "AskKey.slnx");
        var project = Path.Combine(root, "AskKey.Guard.Service", "AskKey.Guard.Service.csproj");
        var versionSource = Path.Combine(root, "AskKey.Integrations", "AskKeyVersion.cs");
        if (!File.Exists(solution)) throw new FileNotFoundException("AskKey.slnx was not found.", solution);
        if (!File.Exists(project)) throw new FileNotFoundException("AskKey.Guard.Service.csproj was not found.", project);
        if (!File.Exists(versionSource)) throw new FileNotFoundException("AskKeyVersion.cs was not found.", versionSource);

        var versionMatch = VersionPattern().Match(File.ReadAllText(versionSource));
        if (!versionMatch.Success)
            throw new InvalidOperationException("Could not read AskKeyVersion.Current.");
        var version = versionMatch.Groups["version"].Value;

        var publishDir = Path.Combine(output, $"AskKey.Guard-{version}-{Runtime}");
        if (Directory.Exists(publishDir))
            throw new InvalidOperationException($"Publish directory already exists: {publishDir}");

        if (!skipTests)
            Run("dotnet", ["test", solution, "-c", "Release", "--nologo", "-v", "q"], root);

        var unsigned = string.IsNullOrWhiteSpace(certificate);
        var publishArgs = new List<string>
        {
            "publish", project, "-c", "Release", "-r", Runtime, "--self-contained", "true",
            "-o", publishDir, "--nologo", "-v", "minimal"
        };
        if (unsigned) publishArgs.Add("-p:AskKeyUnsigned=true");
        Run("dotnet", publishArgs, root);

        var executable = Path.Combine(publishDir, "AskKey.Guard.Service.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The Guard publish output is missing AskKey.Guard.Service.exe.", executable);

        if (!unsigned)
        {
            var signtool = FindSigntool();
            Run(signtool,
                ["sign", "/sha1", certificate!, "/fd", "SHA256", "/tr", timestampUrl, "/td", "SHA256", executable],
                root);
            Run(signtool, ["verify", "/pa", "/all", executable], root);
        }

        Console.WriteLine($"Guard publish: {publishDir}");
        Console.WriteLine($"Guard executable: {executable}");
        if (unsigned)
            Console.WriteLine("Unsigned Guard build: SmartScreen will warn. The app must also be built unsigned.");
        return 0;
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AskKey.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Run the build tool from inside the AskKey repository.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string FindSigntool()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, "signtool.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        if (Directory.Exists(kits))
        {
            var candidate = Directory.EnumerateFiles(kits, "signtool.exe", SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}x64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidate is not null) return candidate;
        }
        throw new FileNotFoundException("signtool.exe was not found. Install the Windows SDK or sign manually.");
    }

    private static void Run(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
    }

    private static string RequiredOption(string[] args, string name)
        => GetOption(args, name) ?? throw new ArgumentException($"Missing required option: {name}");

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AskKey.Build manifest --root <helper-dir> --output <manifest-file>");
        Console.WriteLine("AskKey.Build publish [--output <dir>] [--certificate-thumbprint <thumbprint>] [--timestamp-url <url>] [--skip-tests]");
        Console.WriteLine("AskKey.Build guard [--output <dir>] [--certificate-thumbprint <thumbprint>] [--timestamp-url <url>] [--skip-tests]");
    }

    [GeneratedRegex("Current\\s*=\\s*\"(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)\"")]
    private static partial Regex VersionPattern();
}
