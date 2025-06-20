using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

class Program
{
    static async Task Main()
    {
        string root = Directory.GetCurrentDirectory();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"Found {projects.Count} projects");
        foreach (var proj in projects)
        {
            await ValidateProjectAsync(proj);
        }

        var csFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var file in csFiles)
        {
            await FixSourceAsync(file);
        }

        Console.WriteLine("Processing finished");
    }

    static async Task ValidateProjectAsync(string path)
    {
        var doc = XDocument.Load(path);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var projectDir = Path.GetDirectoryName(path)!;

        string? revitApiPath = doc.Descendants(ns + "RevitApiPath").Select(x => x.Value).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(revitApiPath))
        {
            revitApiPath = Environment.GetEnvironmentVariable("RevitApiPath");
        }

        foreach (var reference in doc.Descendants(ns + "Reference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (include is not ("RevitAPI" or "RevitAPIUI"))
            {
                continue;
            }

            var hint = reference.Element(ns + "HintPath")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(revitApiPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] RevitApiPath not set for '{path}'. Set MSBuild property or environment variable 'RevitApiPath'.");
                Console.ResetColor();
                continue;
            }

            var full = hint.Replace("$(RevitApiPath)", revitApiPath);
            full = Path.GetFullPath(Path.Combine(projectDir, full));
            if (!File.Exists(full))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARN] Missing reference '{full}' in project '{path}'.");
                Console.ResetColor();
            }
        }

        foreach (var pkg in doc.Descendants(ns + "PackageReference"))
        {
            var id = pkg.Attribute("Include")?.Value;
            var version = pkg.Attribute("Version")?.Value ?? pkg.Element(ns + "Version")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
            {
                continue;
            }

            if (!await PackageHasCompatibleFramework(id!, version!))
            {
                var suggestion = await FindLastCompatibleVersion(id!);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARN] Package {id} {version} in {path} lacks net48/netstandard2.0 assets.");
                if (suggestion != null)
                {
                    Console.WriteLine($"       Consider using version {suggestion}.");
                }
                Console.ResetColor();
            }
        }
    }

    static async Task<bool> PackageHasCompatibleFramework(string id, string version)
    {
        var repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var resource = await repo.GetResourceAsync<FindPackageByIdResource>();
        var cache = new SourceCacheContext();
        var v = NuGetVersion.Parse(version);
        using var stream = new MemoryStream();
        if (!await resource.CopyNupkgToStreamAsync(id, v, stream, cache, NullLogger.Instance, CancellationToken.None))
        {
            return false;
        }
        stream.Position = 0;
        using var reader = new PackageArchiveReader(stream);
        var frameworks = reader.GetSupportedFrameworks();
        return frameworks.Any(f => f.GetShortFolderName() == "net48" || f.GetShortFolderName() == "netstandard2.0");
    }

    static async Task<string?> FindLastCompatibleVersion(string id)
    {
        var repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var metadata = await repo.GetResourceAsync<MetadataResource>();
        var versions = await metadata.GetVersions(id, includePrerelease: false, includeUnlisted: false, new SourceCacheContext(), NullLogger.Instance, CancellationToken.None);
        foreach (var v in versions.OrderByDescending(v => v))
        {
            if (await PackageHasCompatibleFramework(id, v.ToNormalizedString()))
            {
                return v.ToNormalizedString();
            }
        }
        return null;
    }

    static async Task FixSourceAsync(string file)
    {
        var originalText = await File.ReadAllTextAsync(file);
        var lines = originalText.Split('\n');
        lines = lines.Where(l => !l.StartsWith("<<<<<<<") && !l.StartsWith("=======") && !l.StartsWith(">>>>>>>")).ToArray();
        var text = string.Join('\n', lines);

        var tree = CSharpSyntaxTree.ParseText(text);
        var root = (CompilationUnitSyntax)await tree.GetRootAsync();

        if (!root.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
        {
            var header = SyntaxFactory.Comment("/// <summary>\n/// Auto generated header\n/// </summary>");
            root = root.WithLeadingTrivia(header, SyntaxFactory.LineFeed);
        }

        var orderedUsings = SyntaxFactory.List(root.Usings.OrderBy(u => u.Name.ToString()));
        root = root.WithUsings(default);

        if (root.Members.FirstOrDefault() is BaseNamespaceDeclarationSyntax ns)
        {
            var newNs = ns.WithUsings(SyntaxFactory.List(ns.Usings.Concat(orderedUsings)));
            root = root.ReplaceNode(ns, newNs);
        }
        else
        {
            root = root.WithUsings(orderedUsings);
        }

        var newText = root.NormalizeWhitespace().ToFullString();
        newText = Regex.Replace(newText, " {2,}", " ");

        var backupPath = file + ".bak";
        File.Copy(file, backupPath, true);
        await File.WriteAllTextAsync(file, newText);
    }
}