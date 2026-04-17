using System.Reflection;

using Lis.Core.Util;

using Microsoft.SemanticKernel;

namespace Lis.Agent;

/// <summary>
/// Maps registered kernel function names to their <see cref="ToolAuthLevel"/>.
/// Built once at startup from an explicit pluginName → Type map, since Semantic
/// Kernel does not propagate custom attributes through KernelFunction metadata
/// reliably across versions. Reading the attributes directly off the plugin
/// class is deterministic and version-independent.
/// </summary>
public sealed class ToolAuthRegistry {
	private readonly Dictionary<string, ToolAuthLevel> map = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Scans each plugin type for methods with <see cref="KernelFunctionAttribute"/>
	/// and caches the <see cref="ToolAuthorizationAttribute"/> level for each.
	/// Call once after all plugins are registered.
	/// </summary>
	public void Build(IReadOnlyDictionary<string, Type> pluginTypes) {
		this.map.Clear();

		foreach ((string pluginName, Type type) in pluginTypes) {
			foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)) {
				KernelFunctionAttribute? kfAttr = method.GetCustomAttribute<KernelFunctionAttribute>();
				if (kfAttr is null) continue;

				string functionName = kfAttr.Name is { Length: > 0 } n ? n : method.Name;
				ToolAuthorizationAttribute? authAttr = method.GetCustomAttribute<ToolAuthorizationAttribute>();
				ToolAuthLevel level = authAttr?.Level ?? ToolAuthLevel.Open;

				this.map[BuildKey(pluginName, functionName)] = level;
			}
		}
	}

	/// <summary>
	/// Looks up the authorization level for a tool call. Handles two SK conventions:
	/// (a) PluginName set + FunctionName bare (older SK): build key from both
	/// (b) PluginName null + FunctionName already prefixed (current SK, "plugin_func"):
	///     use the function name directly as the lookup key
	/// Returns <see cref="ToolAuthLevel.Open"/> if not found.
	/// </summary>
	public ToolAuthLevel GetLevel(string? pluginName, string functionName) {
		string key = string.IsNullOrEmpty(pluginName) ? functionName : BuildKey(pluginName, functionName);
		return this.map.TryGetValue(key, out ToolAuthLevel level) ? level : ToolAuthLevel.Open;
	}

	/// <summary>All registered (pluginName, functionName, level) triples — useful for startup logging.</summary>
	public IEnumerable<(string Plugin, string Function, ToolAuthLevel Level)> Entries() {
		foreach ((string key, ToolAuthLevel level) in this.map) {
			int sep = key.IndexOf('_');
			if (sep < 0) continue;
			yield return (key[..sep], key[(sep + 1)..], level);
		}
	}

	// Separator matches Semantic Kernel's flattened FunctionName convention ("plugin_function"),
	// so a PluginName=null lookup with an already-prefixed function name still hits.
	private static string BuildKey(string pluginName, string functionName) =>
		$"{pluginName}_{functionName}";
}
