using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Colony.Plugins.Native;

/// <summary>
/// A Plugin implemented against the C ABI (spec section 8): a shared library exporting
/// <c>colony_plugin_get(const colony_api*, colony_plugin_desc*)</c>, or a <c>colony_plugin_desc</c> a C host adds
/// through <c>colony_host_add_plugin</c>. The host opts in by adding one to a <see cref="PluginHost"/> like any
/// C# Plugin; its <c>register</c> callback registers Components, Resources, events, Input Commands, Definitions
/// and Systems through the function table, and its Systems run under the same scheduler with the access they
/// declared in data. Caveat (spec section 8): a Plugin that is itself a Native AOT .NET library carries its own
/// runtime; two runtimes in one process are untested.
/// </summary>
public sealed unsafe class NativePlugin : IPlugin
{
    private readonly delegate* unmanaged<void*, nint, int> _register;
    private readonly void* _userData;

    internal NativePlugin(in ColonyPluginDesc desc)
    {
        if (desc.AbiVersion != ColonyAbi.Version)
        {
            throw new PluginException(Utf8Strings.Read(desc.Id) ?? "?", $"Native Plugin '{Utf8Strings.Read(desc.Id)}' was built against C ABI version {desc.AbiVersion}; this host provides {ColonyAbi.Version}.");
        }

        if (desc.Register == null)
        {
            throw new PluginException(Utf8Strings.Read(desc.Id) ?? "?", $"Native Plugin '{Utf8Strings.Read(desc.Id)}' has no register callback.");
        }

        var dependencies = ImmutableArray.CreateBuilder<string>(Math.Max(desc.DependencyCount, 0));
        for (int i = 0; i < desc.DependencyCount; i++)
        {
            dependencies.Add(Utf8Strings.ReadRequired(desc.Dependencies[i], "dependency"));
        }

        Manifest = new PluginManifest
        {
            Id = Utf8Strings.ReadRequired(desc.Id, "colony_plugin_desc.id"),
            Version = Utf8Strings.Read(desc.Version) ?? "0.0.0",
            Deterministic = desc.Deterministic != 0,
            AbiVersion = (int)desc.AbiVersion,
            Dependencies = dependencies.ToImmutable(),
        };
        _register = desc.Register;
        _userData = desc.UserData;
    }

    public PluginManifest Manifest { get; }

    /// <summary>
    /// Loads the shared library at <paramref name="path"/>, asks its <c>colony_plugin_get</c> for the Plugin
    /// descriptor (handing it the function table) and wraps it. The library stays loaded for the process lifetime
    /// (spec section 8: no runtime unloading in v1).
    /// </summary>
    public static NativePlugin Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        nint library = NativeLibrary.Load(path);
        nint export = NativeLibrary.GetExport(library, "colony_plugin_get");
        ColonyPluginDesc desc = default;
        int status = ((delegate* unmanaged<nint, ColonyPluginDesc*, int>)export)(ColonyAbi.Table, &desc);
        if (status != ColonyAbi.Ok)
        {
            throw new PluginException($"colony_plugin_get of '{path}' returned status {status}.");
        }

        return new NativePlugin(in desc);
    }

    public void Register(PluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var native = new NativeRegistration(registration, this);
        nint handle = NativeHandles.GetOrAdd(native);
        try
        {
            int status = _register(_userData, handle);
            if (status != ColonyAbi.Ok)
            {
                throw new PluginException(Manifest.Id, $"Native Plugin '{Manifest.Id}' returned status {status} from register: {ColonyAbi.LastError}");
            }
        }
        finally
        {
            NativeHandles.Release(native);
        }
    }
}
