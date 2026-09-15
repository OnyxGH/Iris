using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Iris.CodingAgent.Config;
using Iris.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Iris.CodingAgent.Core.Extensions;

/// <summary>An extension compiled into Iris itself, enabled unless <c>builtinExtensions.&lt;id&gt;</c> is false in settings.</summary>
public sealed record BuiltinExtension(string Id, Func<IExtension> Create);

/// <summary>Built-in extensions registered by the host application before sessions are created.</summary>
public static class BuiltinExtensions
{
    private static readonly List<BuiltinExtension> Registered = [];

    public static IReadOnlyList<BuiltinExtension> All
    {
        get
        {
            lock (Registered) return [.. Registered];
        }
    }

    public static void Register(string id, Func<IExtension> create)
    {
        lock (Registered)
        {
            Registered.RemoveAll(e => e.Id == id);
            Registered.Add(new BuiltinExtension(id, create));
        }
    }
}

public sealed record ExtensionLoadError(string Path, string Error);

public sealed class ExtensionLoadResult
{
    public static ExtensionLoadResult Empty { get; } = new([], [], new ExtensionRuntime(), []);

    internal ExtensionLoadResult(List<LoadedExtension> extensions, List<ExtensionLoadError> errors, ExtensionRuntime runtime, List<AssemblyLoadContext> contexts)
    {
        Extensions = extensions;
        Errors = errors;
        Runtime = runtime;
        Contexts = contexts;
    }

    public IReadOnlyList<LoadedExtension> Extensions { get; }

    public IReadOnlyList<ExtensionLoadError> Errors { get; }

    public ExtensionRuntime Runtime { get; }

    internal IReadOnlyList<AssemblyLoadContext> Contexts { get; }

    /// <summary>Invalidate the extensions and unload their assemblies (after a reload replaced them).</summary>
    public void Unload()
    {
        Runtime.Invalidate();
        foreach (var context in Contexts)
        {
            try
            {
                context.Unload();
            }
            catch (InvalidOperationException)
            {
                // Not collectible.
            }
        }
    }
}

/// <summary>
/// Loads C# extensions: single .cs files and folders of .cs files are compiled in memory with Roslyn (cached by content
/// hash), folders with a DLL named after the folder and standalone .dll files are loaded as prebuilt assemblies. Each
/// extension gets its own collectible load context; Iris assemblies are shared with the host.
/// </summary>
public static class ExtensionLoader
{
    private static readonly string[] ImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading",
        "System.Threading.Tasks", "System.Text.Json", "System.Text.Json.Nodes", "Iris.Ai", "Iris.Extensions", "Iris.Tui",
        "Iris.Tui.Components", "Iris.CodingAgent.Modes.Interactive",
    ];

    private static readonly Lazy<IReadOnlyList<MetadataReference>> HostReferences = new(CreateHostReferences);

    public static async Task<ExtensionLoadResult> LoadAsync(IEnumerable<ResolvedResource> entries, string cwd, string agentDir, SettingsManager? settingsManager, CancellationToken cancellationToken = default)
    {
        var runtime = new ExtensionRuntime();
        var extensions = new List<LoadedExtension>();
        var errors = new List<ExtensionLoadError>();
        var contexts = new List<AssemblyLoadContext>();

        foreach (var builtin in BuiltinExtensions.All)
        {
            if (settingsManager?.IsBuiltinExtensionEnabled(builtin.Id) == false) continue;
            var path = $"<builtin:{builtin.Id}>";
            Register(path, SourceInfo.Synthetic(path, "builtin", "user"), builtin.Create, runtime, cwd, extensions, errors);
        }

        foreach (var entry in entries.Where(e => e.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (assembly, context) = await LoadAssemblyAsync(entry.Path, agentDir, cancellationToken);
                contexts.Add(context);
                var types = assembly.GetExportedTypes()
                    .Where(t => typeof(IExtension).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null)
                    .ToList();
                if (types.Count == 0)
                {
                    errors.Add(new ExtensionLoadError(entry.Path, "No public class implementing Iris.Extensions.IExtension with a parameterless constructor was found."));
                    continue;
                }
                var sourceInfo = SourceInfo.FromMetadata(entry.Path, entry.Metadata);
                foreach (var type in types)
                {
                    Register(entry.Path, sourceInfo, () => (IExtension)Activator.CreateInstance(type)!, runtime, cwd, extensions, errors);
                }
            }
            catch (ExtensionCompilationException ex)
            {
                errors.Add(new ExtensionLoadError(entry.Path, ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new ExtensionLoadError(entry.Path, ex.Message));
            }
        }
        return new ExtensionLoadResult(extensions, errors, runtime, contexts);
    }

    private static void Register(string path, SourceInfo sourceInfo, Func<IExtension> create, ExtensionRuntime runtime, string cwd, List<LoadedExtension> extensions, List<ExtensionLoadError> errors)
    {
        var extension = new LoadedExtension(path, sourceInfo);
        var api = new ExtensionApi(extension, runtime, cwd);
        try
        {
            create().Register(api);
            api.FinishLoading();
            extensions.Add(extension);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } innerException } ? innerException : ex;
            errors.Add(new ExtensionLoadError(path, $"Failed to register extension: {inner.Message}"));
        }
    }

    // ----- Assemblies -----

    private sealed class ExtensionLoadContext(string name, string? directory) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share everything the host already has (Iris, the runtime, bundled libraries) so types match.
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName))) return null;
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll"))) return null;
            if (directory is null) return null;
            var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(candidate))) : null;
        }
    }

    internal static async Task<(Assembly Assembly, AssemblyLoadContext Context)> LoadAssemblyAsync(string path, string agentDir, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var context = new ExtensionLoadContext(full, Path.GetDirectoryName(full));
            var pdb = Path.ChangeExtension(full, ".pdb");
            return (LoadBytes(context, await File.ReadAllBytesAsync(full, cancellationToken), File.Exists(pdb) ? await File.ReadAllBytesAsync(pdb, cancellationToken) : null), context);
        }

        List<string> sources;
        string? directory;
        if (File.Exists(full) && full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            sources = [full];
            directory = Path.GetDirectoryName(full);
        }
        else if (Directory.Exists(full))
        {
            directory = full;
            var dll = Path.Combine(full, Path.GetFileName(full) + ".dll");
            if (File.Exists(dll)) return await LoadAssemblyAsync(dll, agentDir, cancellationToken);
            sources = Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(full, f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (sources.Count == 0) throw new ExtensionCompilationException($"Extension folder contains no .cs files or {Path.GetFileName(dll)}.");
        }
        else
        {
            throw new FileNotFoundException($"Extension path does not exist: {full}");
        }

        var siblingDlls = directory is null ? [] : Directory.EnumerateFiles(directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var (peBytes, pdbBytes) = await CompileCachedAsync(full, sources, siblingDlls, agentDir, cancellationToken);
        var loadContext = new ExtensionLoadContext(full, directory);
        return (LoadBytes(loadContext, peBytes, pdbBytes), loadContext);
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal) || relative.Contains("/bin/") || relative.Contains("/obj/");
    }

    private static Assembly LoadBytes(AssemblyLoadContext context, byte[] pe, byte[]? pdb)
    {
        using var peStream = new MemoryStream(pe);
        using var pdbStream = pdb is null ? null : new MemoryStream(pdb);
        return context.LoadFromStream(peStream, pdbStream);
    }

    // ----- Compilation -----

    private static async Task<(byte[] Pe, byte[] Pdb)> CompileCachedAsync(string rootPath, List<string> sources, List<string> siblingDlls, string agentDir, CancellationToken cancellationToken)
    {
        var contents = new List<(string Path, string Text)>();
        foreach (var source in sources) contents.Add((source, await File.ReadAllTextAsync(source, cancellationToken)));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        Add(AppConfig.Version);
        Add(typeof(IExtension).Assembly.ManifestModule.ModuleVersionId.ToString());
        foreach (var (path, text) in contents)
        {
            Add(Path.GetRelativePath(rootPath, path));
            Add(text);
        }
        foreach (var dll in siblingDlls)
        {
            var info = new FileInfo(dll);
            Add($"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        var key = Convert.ToHexStringLower(hash.GetHashAndReset());
        var cacheDir = Path.Combine(agentDir, "cache", "extensions");
        var cachedPe = Path.Combine(cacheDir, key + ".dll");
        var cachedPdb = Path.Combine(cacheDir, key + ".pdb");
        if (File.Exists(cachedPe) && File.Exists(cachedPdb))
        {
            return (await File.ReadAllBytesAsync(cachedPe, cancellationToken), await File.ReadAllBytesAsync(cachedPdb, cancellationToken));
        }

        var (pe, pdb) = Compile(Path.GetFileNameWithoutExtension(rootPath), contents, siblingDlls, cancellationToken);
        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(cachedPdb, pdb, cancellationToken);
            await File.WriteAllBytesAsync(cachedPe, pe, cancellationToken);
        }
        catch (IOException)
        {
            // Caching is best-effort.
        }
        return (pe, pdb);
    }

    internal static (byte[] Pe, byte[] Pdb) Compile(string name, IReadOnlyList<(string Path, string Text)> sources, IReadOnlyList<string> extraReferences, CancellationToken cancellationToken = default)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, parseOptions, s.Path, Encoding.UTF8, cancellationToken)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText(string.Concat(ImplicitUsings.Select(u => $"global using global::{u};\n")), parseOptions, "IrisExtensionGlobalUsings.g.cs", Encoding.UTF8, cancellationToken));

        var references = new List<MetadataReference>(HostReferences.Value);
        foreach (var dll in extraReferences)
        {
            if (IsManagedAssembly(dll)) references.Add(MetadataReference.CreateFromFile(dll));
        }

        var assemblyName = $"IrisExtension.{SanitizeName(name)}.{Guid.NewGuid():N}";
        var compilation = CSharpCompilation.Create(assemblyName, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable,
            optimizationLevel: OptimizationLevel.Release));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: cancellationToken);
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .Select(FormatDiagnostic);
            throw new ExtensionCompilationException("Compilation failed:\n" + string.Join("\n", errors));
        }
        return (pe.ToArray(), pdb.ToArray());
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetMappedLineSpan();
        var location = span.IsValid ? $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})" : "";
        return $"{location}: error {diagnostic.Id}: {diagnostic.GetMessage()}";
    }

    private static string SanitizeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static IReadOnlyList<MetadataReference> CreateHostReferences()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }
        return byName.Values.Where(IsManagedAssembly).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ExtensionCompilationException(string message) : Exception(message);
