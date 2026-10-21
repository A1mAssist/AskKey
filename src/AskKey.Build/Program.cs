using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

return await BuildTool.RunAsync(args);

internal static partial class BuildTool
{
    private const string Runtime = "win-x64";
    private const string InstallerTextDirectory = "src\\installer\\text";

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
                "bundle" => RunBundle(args[1..]),
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

            // Installers ship runtime files only and drop symbols; a manifest that
            // lists PDBs can never match the installed helper payload.
            if (string.Equals(Path.GetExtension(file), ".pdb", StringComparison.OrdinalIgnoreCase)) continue;

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
        var project = Path.Combine(root, "src", "AskKey.WinUI", "AskKey.WinUI.csproj");
        var versionSource = Path.Combine(root, "src", "AskKey.Integrations", "AskKeyVersion.cs");
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

        var buildRoot = Path.Combine(root, "src", "AskKey.WinUI", "bin", "x64", "Release");
        var buildDir = Directory.EnumerateDirectories(buildRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "AskKey.exe"))
                && File.Exists(Path.Combine(directory, "App.xbf")))
            .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "AskKey.exe")))
            .FirstOrDefault()
            ?? throw new DirectoryNotFoundException($"Build output was not found under {buildRoot}.");

        Directory.CreateDirectory(publishDir);
        CopyDirectory(buildDir, publishDir);

        // XAML satellite assemblies land under <root>\win-x64\<lang>, not directly under
        // the publish root, so a root-only sweep leaves every language but zh-CN in place.
        foreach (var directory in Directory.EnumerateDirectories(publishDir, "*",
                     SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "zh-CN", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(directory, "Microsoft.ui.xaml.dll.mui"))
                || Directory.EnumerateFiles(directory, "*.mui", SearchOption.TopDirectoryOnly).Any())
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // Self-contained WinUI already carries the Windows runtime; the other RIDs ship
        // Linux, macOS and wasm binaries that can never run on this target.
        var runtimes = Path.Combine(publishDir, "runtimes");
        if (Directory.Exists(runtimes))
        {
            foreach (var rid in Directory.EnumerateDirectories(runtimes)
                         .Where(d => !string.Equals(Path.GetFileName(d), Runtime, StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Delete(rid, recursive: true);
            }
        }

        // The build root also holds the RID-specific intermediate tree, so the copy above
        // brought a second, older copy of the whole payload in. Ship only the payload root.
        var intermediate = Path.Combine(publishDir, Runtime);
        if (Directory.Exists(intermediate))
        {
            Directory.Delete(intermediate, recursive: true);
        }

        var strayLocales = Directory.EnumerateDirectories(publishDir, "*", SearchOption.AllDirectories)
            .Where(d => !string.Equals(Path.GetFileName(d), "zh-CN", StringComparison.OrdinalIgnoreCase))
            .Where(d => Directory.EnumerateFiles(d, "*.mui", SearchOption.TopDirectoryOnly).Any())
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (strayLocales.Count > 0)
        {
            throw new InvalidOperationException(
                "Only zh-CN may ship, but these locales survived the trim: " + string.Join(", ", strayLocales));
        }

        // Symbols are build artifacts; shipping them puts debug data in Program Files.
        foreach (var symbol in Directory.EnumerateFiles(publishDir, "*.pdb", SearchOption.AllDirectories))
        {
            File.Delete(symbol);
        }

        // A second host binary anywhere under the payload means an intermediate tree was
        // copied in; fail the build rather than shipping a stale copy in Program Files.
        // The filename match is case-insensitive on Windows, so the helper's own
        // Helpers\askkey.exe matches too: allow the two fixed slots and nothing else.
        var expectedHosts = new[]
        {
            Path.Combine(publishDir, "AskKey.exe"),
            Path.Combine(publishDir, "Helpers", "askkey.exe"),
        };
        var duplicateHosts = Directory.EnumerateFiles(publishDir, "AskKey.exe", SearchOption.AllDirectories)
            .Where(path => !expectedHosts.Any(host => string.Equals(path, host,
                StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (duplicateHosts.Count > 0)
        {
            throw new InvalidOperationException(
                "The payload contains more than one host executable: "
                + string.Join(", ", duplicateHosts.Select(path => Path.GetRelativePath(publishDir, path))));
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
            "--icon", Path.Combine(root, "src", "AskKey.WinUI", "Assets", "AskKey.ico"),
            "--msi", "--instLocation", "PerMachine",
            // Show the product icon and plain-text guidance in the installer itself instead
            // of making the user follow LicenseUrl out to the web mid-install.
            "--instWelcome", Path.Combine(root, InstallerTextDirectory, "welcome-zh-CN.txt"),
            "--instReadme", Path.Combine(root, InstallerTextDirectory, "readme-zh-CN.txt")
        };
        if (!unsigned)
        {
            packArgs.Add("--signParams");
            packArgs.Add($"/sha1 {certificate} /fd SHA256 /tr {timestampUrl} /td SHA256");
        }
        Run(FindOnPath("vpk.exe"), packArgs, root);

        var guardArgs = new List<string> { "--output", output, "--skip-tests" };
        if (!unsigned)
        {
            guardArgs.Add("--certificate-thumbprint");
            guardArgs.Add(certificate!);
            guardArgs.Add("--timestamp-url");
            guardArgs.Add(timestampUrl);
        }
        var guardExit = await RunGuardAsync(guardArgs.ToArray());
        if (guardExit != 0) return guardExit;

        var guardPublishDir = Path.Combine(output, $"AskKey.Guard-{version}-{Runtime}");
        var guardMsi = BuildGuardMsi(root, version, guardPublishDir, releaseDir);
        if (!unsigned) SignFile(root, guardMsi, certificate!, timestampUrl);

        var bundle = BuildBundle(root, version, Path.Combine(releaseDir, "AskKey-win.msi"), guardMsi, releaseDir);
        if (!unsigned) SignFile(root, bundle, certificate!, timestampUrl);

        Console.WriteLine($"Published: {publishDir}");
        Console.WriteLine($"Release:   {releaseDir}");

        // An unsigned release asks for UAC and installs a service, so users need a way to
        // check what they downloaded. Emit SHA-256 next to every asset.
        var checksums = new List<string>();
        foreach (var file in Directory.EnumerateFiles(releaseDir)
                     .Where(path => !path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(file);
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            checksums.Add($"{digest}  {Path.GetFileName(file)}");
        }
        if (checksums.Count > 0)
        {
            var checksumPath = Path.Combine(releaseDir, $"AskKey-{version}.sha256");
            File.WriteAllText(checksumPath, string.Join('\n', checksums) + "\n", new UTF8Encoding(false));
        }

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
        var project = Path.Combine(root, "src", "AskKey.Guard.Service", "AskKey.Guard.Service.csproj");
        var versionSource = Path.Combine(root, "src", "AskKey.Integrations", "AskKeyVersion.cs");
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

    private static int RunBundle(string[] args)
    {
        var root = FindRepoRoot();
        var version = ReadVersion(root);
        var releaseDir = GetOption(args, "--release") ?? Path.Combine(root, "artifacts", "publish", "release");
        var appMsi = GetOption(args, "--app-msi") ?? Path.Combine(releaseDir, "AskKey-win.msi");
        var guardMsi = GetOption(args, "--guard-msi")
            ?? Path.Combine(root, "src", "installer", "Guard", "bin", "Release", $"AskKeyGuard-{version}.msi");
        var bundle = BuildBundle(root, version, appMsi, guardMsi, releaseDir);
        Console.WriteLine($"Bundle: {bundle}");
        return 0;
    }

    private static string ReadVersion(string root)
    {
        var versionSource = Path.Combine(root, "src", "AskKey.Integrations", "AskKeyVersion.cs");
        if (!File.Exists(versionSource)) throw new FileNotFoundException("AskKeyVersion.cs was not found.", versionSource);
        var versionMatch = VersionPattern().Match(File.ReadAllText(versionSource));
        if (!versionMatch.Success)
            throw new InvalidOperationException("Could not read AskKeyVersion.Current.");
        return versionMatch.Groups["version"].Value;
    }

    private static string BuildGuardMsi(string root, string version, string publishDir, string outputDir)
    {
        var project = Path.Combine(root, "src", "installer", "Guard", "AskKey.Guard.Installer.wixproj");
        if (!File.Exists(project))
            throw new FileNotFoundException("AskKey.Guard.Installer.wixproj was not found.", project);
        // Rebuild, not build: the payload arrives as a command-line property MSBuild never tracks.
        Run("dotnet", ["build", project, "-c", "Release", "--nologo", "-v", "minimal", "-t:Rebuild",
            $"-p:ProductVersion={version}", $"-p:GuardPublishDir={Path.GetFullPath(publishDir)}"], root);
        var built = Path.Combine(root, "src", "installer", "Guard", "bin", "Release", $"AskKeyGuard-{version}.msi");
        if (!File.Exists(built)) throw new FileNotFoundException("The Guard MSI was not produced.", built);
        Directory.CreateDirectory(outputDir);
        var target = Path.Combine(outputDir, Path.GetFileName(built));
        if (!string.Equals(Path.GetFullPath(built), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(built, target, overwrite: true);
        return target;
    }

    /// <summary>WiX wants an RTF license, the repo ships plain text. Derive one so the
    /// installer can never show a license that has drifted from LICENSE.</summary>
    private static string WriteLicenseRtf(string root)
    {
        var source = Path.Combine(root, "LICENSE");
        if (!File.Exists(source)) throw new FileNotFoundException("LICENSE was not found.", source);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = File.ReadAllText(source).Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        var body = text.Replace("\r\n", "\\par ").Replace("\n", "\\par ");
        var rtf = "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 Calibri;}}"
            + "\\viewkind4\\uc1\\pard\\f0\\fs18 " + body + "\\par }";
        var path = Path.Combine(root, InstallerTextDirectory, "LICENSE.rtf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, rtf, Encoding.GetEncoding(1252));
        return path;
    }

    /// <summary>The stock BA theme draws its image at 64x64, so ship the app icon at that size.</summary>
    private static string WriteBundleLogo(string root)
    {
        var source = Path.Combine(root, "src", "AskKey.WinUI", "Assets", "AppIcon.png");
        if (!File.Exists(source)) throw new FileNotFoundException("AppIcon.png was not found.", source);
        var path = Path.Combine(root, InstallerTextDirectory, "logo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var input = System.Drawing.Image.FromFile(source);
        using var output = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(input, new Rectangle(0, 0, 64, 64));
        }
        output.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static string BuildBundle(string root, string version, string appMsi, string guardMsi, string outputDir)
    {
        var project = Path.Combine(root, "src", "installer", "Bundle", "AskKey.Bundle.wixproj");
        if (!File.Exists(project))
            throw new FileNotFoundException("AskKey.Bundle.wixproj was not found.", project);
        if (!File.Exists(appMsi)) throw new FileNotFoundException("The app MSI was not found.", appMsi);
        if (!File.Exists(guardMsi)) throw new FileNotFoundException("The Guard MSI was not found.", guardMsi);
        var licenseRtf = WriteLicenseRtf(root);
        var bundleLogo = WriteBundleLogo(root);
        // A bundle whose own version disagrees with its payload installs as the newer
        // number and then refuses the downgrade, so catch the mismatch here instead.
        foreach (var payload in new[] { ("app MSI", appMsi), ("Guard MSI", guardMsi) })
        {
            var payloadVersion = ReadMsiProductVersion(payload.Item2);
            if (!string.Equals(payloadVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The {payload.Item1} is version {payloadVersion}, but the bundle would be "
                    + $"version {version}. Rebuild it, or point the bundle at the matching package.");
            }
        }
        // Rebuild, not build: MSBuild does not track files that arrive through command-line
        // properties, so an incremental build can ship a bundle with the previous MSI inside.
        Run("dotnet", ["build", project, "-c", "Release", "--nologo", "-v", "minimal", "-t:Rebuild",
            $"-p:ProductVersion={version}", $"-p:AppMsi={Path.GetFullPath(appMsi)}",
            $"-p:GuardMsi={Path.GetFullPath(guardMsi)}", $"-p:LicenseFile={licenseRtf}",
            $"-p:BaLogo={bundleLogo}"], root);
        var built = Path.Combine(root, "src", "installer", "Bundle", "bin", "Release", $"AskKeySetup-{version}.exe");
        if (!File.Exists(built)) throw new FileNotFoundException("The setup bundle was not produced.", built);
        Directory.CreateDirectory(outputDir);
        var target = Path.Combine(outputDir, Path.GetFileName(built));
        if (!string.Equals(Path.GetFullPath(built), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(built, target, overwrite: true);
        return target;
    }

    private static void SignFile(string root, string path, string certificate, string timestampUrl)
    {
        var signtool = FindSigntool();
        Run(signtool,
            ["sign", "/sha1", certificate, "/fd", "SHA256", "/tr", timestampUrl, "/td", "SHA256", path],
            root);
        Run(signtool, ["verify", "/pa", "/all", path], root);
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

    /// <summary>
    /// Reads ProductVersion from an MSI and returns its first three fields. An app MSI
    /// written by Velopack carries four (1.0.0.0) while the bundle version has three,
    /// so a full-string comparison would reject a correct package.
    /// </summary>
    private static string ReadMsiProductVersion(string msiPath)
    {
        var database = IntPtr.Zero;
        var view = IntPtr.Zero;
        try
        {
            if (MsiOpenDatabaseW(msiPath, null, ref database) != 0)
                throw new InvalidOperationException($"The app MSI could not be opened: {msiPath}");
            if (MsiDatabaseOpenViewW(database,
                    "SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'", ref view) != 0)
            {
                throw new InvalidOperationException($"The app MSI has no readable ProductVersion: {msiPath}");
            }
            if (MsiViewExecute(view, IntPtr.Zero) != 0)
                throw new InvalidOperationException($"The app MSI version query failed: {msiPath}");

            var record = IntPtr.Zero;
            if (MsiViewFetch(view, ref record) != 0)
                throw new InvalidOperationException($"The app MSI does not declare a ProductVersion: {msiPath}");
            try
            {
                var buffer = new StringBuilder(64);
                var length = (uint)buffer.Capacity;
                if (MsiRecordGetStringW(record, 1, buffer, ref length) != 0) return string.Empty;
                var fields = buffer.ToString().Split('.');
                return string.Join('.', fields.Take(3));
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            if (view != IntPtr.Zero) MsiCloseHandle(view);
            if (database != IntPtr.Zero) MsiCloseHandle(database);
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiOpenDatabaseW(string path, string? persist, ref IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiDatabaseOpenViewW(IntPtr database, string query, ref IntPtr view);

    [DllImport("msi.dll")]
    private static extern int MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern int MsiViewFetch(IntPtr view, ref IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiRecordGetStringW(IntPtr record, uint field, StringBuilder buffer, ref uint length);

    [DllImport("msi.dll")]
    private static extern int MsiCloseHandle(IntPtr handle);

    /// <summary>Resolves a build tool on PATH, or says how to install it.</summary>
    private static string FindOnPath(string toolName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, toolName);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"{toolName} was not found on PATH. Install it with: dotnet tool install --global vpk");
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
        Console.WriteLine("AskKey.Build bundle [--release <dir>] [--app-msi <path>] [--guard-msi <path>]");
    }

    [GeneratedRegex("Current\\s*=\\s*\"(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)\"")]
    private static partial Regex VersionPattern();
}
